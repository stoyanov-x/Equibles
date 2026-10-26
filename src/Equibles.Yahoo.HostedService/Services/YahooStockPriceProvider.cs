using Equibles.CommonStocks.Data.Models;
using Equibles.Core.Contracts;
using Equibles.Data;
using Equibles.Data.Extensions;
using Equibles.Yahoo.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Equibles.Yahoo.HostedService.Services;

public class YahooStockPriceProvider : IStockPriceProvider
{
    private const int LookbackDays = 7;

    private readonly EquiblesFinancialDbContext _dbContext;

    public YahooStockPriceProvider(EquiblesFinancialDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<
        Dictionary<(Guid CommonStockId, string ListedTicker, DateOnly Date), decimal>
    > GetClosingPrices(
        IEnumerable<(Guid CommonStockId, string ListedTicker, DateOnly Date)> requests,
        CancellationToken cancellationToken = default
    )
    {
        var result = new Dictionary<(Guid, string, DateOnly), decimal>();
        var requestList = requests.ToList();
        if (requestList.Count == 0)
            return result;

        // Group by date so we can batch-query per reporting period
        var byDate = requestList
            .GroupBy(r => r.Date)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => (r.CommonStockId, r.ListedTicker)).Distinct().ToList()
            );

        foreach (var (date, listings) in byDate)
        {
            var minDate = date.AddDays(-LookbackDays);
            var stockIds = listings.Select(l => l.CommonStockId).Distinct().ToList();
            // The stored series ticker is normalized uppercase; fold the request the same way
            // so one non-canonical spelling can't silently park a class's positions pending.
            var secondaryTickers = listings
                .Where(l => l.ListedTicker != null)
                .Select(l => l.ListedTicker.ToUpperInvariant())
                .Distinct()
                .ToList();

            // One window query per date: the primary series for every requested stock, plus
            // the exact secondary series requested. The precise (stock, listing) pairing is
            // re-applied in memory — the small overfetch beats a per-pair query.
            // Resolve the candidate listings FIRST, then filter prices by listing id.
            //
            // WHY: the original query filtered prices with
            //     stockIds.Contains(p.Listing.Security.EquityIssuerId)
            // but the issuer sits two joins away (Price -> Listing -> Security), so the
            // predicate is not sargable. The planner could not push it into the
            // (EquityListingId, Date) index and had to take the whole 7-day slice of
            // EquityDailyStockPrice - roughly 15.7k listings x 7 days per call - then join
            // every row to reach the issuer. Measured in production: 4,445 calls at 43.4 s
            // mean, 53.5 hours of database time in total.
            //
            // EquityListing is only ~15.7k rows, so materialising the id set costs one small
            // index-backed query, and the price scan then becomes
            //     "EquityListingId" = ANY(...)  AND  "Date" BETWEEN ...
            // which IX_EquityDailyStockPrice_EquityListingId_Date can serve directly.
            //
            // The predicates are unchanged, only relocated: every one of them depends solely
            // on the listing, so evaluating them over EquityListing yields the same set of
            // rows as evaluating them per price row.
            var candidateListingIds = await _dbContext
                .Set<EquityListing>()
                .Where(l => l.MarketCountryCode == "US")
                .Where(l =>
                    l.Presentation != null
                    || !_dbContext
                        .Set<EquityListing>()
                        .Any(other =>
                            other.Id != l.Id
                            && other.MarketCountryCode == "US"
                            && other.Ticker == l.Ticker
                            && other.Security.EquityIssuerId == l.Security.EquityIssuerId
                        )
                )
                .Where(l => stockIds.Contains(l.Security.EquityIssuerId))
                .Select(l => l.Id)
                .ToListAsync(cancellationToken);

            if (candidateListingIds.Count == 0)
                continue;

            var prices = await _dbContext
                .Set<EquityDailyStockPrice>()
                .Where(p => candidateListingIds.Contains(p.EquityListingId))
                .Where(p =>
                    (p.Listing.Presentation != null || secondaryTickers.Contains(p.SourceTicker))
                    && p.Date >= minDate
                    && p.Date <= date
                    && p.Volume > 0
                )
                .Select(p => new
                {
                    CommonStockId = p.Listing.Security.EquityIssuerId,
                    ListedTicker = p.SourceTicker,
                    PrimaryTicker = p.Listing.Security.Issuer.Presentation.Listing.Ticker,
                    p.Date,
                    p.Close,
                })
                .ToListAsync(cancellationToken);

            // Pick the latest price per exact series
            var latestBySeries = prices.LatestPerGroup(
                p => (p.CommonStockId, p.ListedTicker),
                p => p.Date
            );
            var byRequestedListing = latestBySeries.ToDictionary(p =>
                (
                    p.CommonStockId,
                    // The caller addresses the primary series as null; a secondary by its
                    // symbol, folded uppercase to match the request normalization above.
                    ListedTicker: string.Equals(
                        p.ListedTicker,
                        p.PrimaryTicker,
                        StringComparison.OrdinalIgnoreCase
                    )
                        ? null
                        : p.ListedTicker.ToUpperInvariant()
                )
            );

            foreach (var listing in listings)
            {
                var lookup = (listing.CommonStockId, listing.ListedTicker?.ToUpperInvariant());
                if (byRequestedListing.TryGetValue(lookup, out var price))
                {
                    result[(listing.CommonStockId, listing.ListedTicker, date)] = price.Close;
                }
            }
        }

        return result;
    }
}
