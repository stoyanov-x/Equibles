using System.ComponentModel.DataAnnotations;
using Equibles.Data.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Equibles.CommonStocks.Data.Models;

// Stable identity for a security traded on a venue, independent of any public URL.
public class EquityListing : IActivable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public virtual List<EquityListingSourceIdentifier> SourceIdentifiers { get; set; } = [];
    public Guid EquitySecurityId { get; set; }
    public virtual EquitySecurity Security { get; set; }
    public virtual List<EquityListingTickerAlias> TickerAliases { get; set; } = [];

    // Present only when this listing is its issuer's default listing; readers test
    // `Presentation != null` instead of walking Security.Issuer.Presentation.
    public virtual EquityIssuerPresentation Presentation { get; set; }

    [MaxLength(4)]
    public string MarketIdentifierCode { get; set; }

    // The venue's country, independently of the issuer's domicile or quotation currency.
    [MaxLength(2)]
    public string MarketCountryCode { get; set; }

    [Required, MaxLength(32)]
    public string Ticker { get; set; }

    [MaxLength(3)]
    public string TradingCurrency { get; set; }

    // Multiply the source quote by this value to obtain major currency units.
    // Required evidence: do not default an unknown quotation scale to one.
    [Precision(18, 8)]
    public decimal? QuoteUnitMultiplier { get; set; }
    public EquityIdentityState IdentityState { get; set; }

    public bool Active { get; set; } = true;
    public DateOnly? ListedOn { get; set; }
    public DateOnly? DelistedOn { get; set; }

    public bool IsDirectoryListed { get; set; }
    public bool IsReferenceListed { get; set; }
    public bool PriceHistoryBackfilled { get; set; }
    public DateTime? YahooEnrichmentAttemptedAt { get; set; }

    // Last Yahoo chart request for this listing; bounds the cadence once a venue keeps the series current.
    public DateTime? YahooPriceSyncAttemptedAt { get; set; }

    // Newest stored bar for this listing, kept here rather than aggregated from the price table.
    // Deriving it per cycle meant `max(Date) GROUP BY listing` over ~10k listings, which reads every
    // index page of every series: measured at ~15 cold pages per listing on the production host, so a
    // catalog cycle could not finish inside its 300 s command timeout and had never completed once.
    // Maintained by the AFTER INSERT trigger on EquityDailyStockPrice so no writer can forget it; a
    // null means the backfill has not reached this listing yet and callers must fall back to the
    // price table for it.
    public DateOnly? LastPriceDate { get; set; }

    // The ticker the newest bar was stored under, which is how a venue-maintained series is told
    // apart from one this lane wrote. It came out of the same aggregate as LastPriceDate, so it is
    // kept beside it rather than re-derived from the price table.
    public string LastPriceSource { get; set; }
    public DateTime? HistoricalPriceBackfillAttemptedAt { get; set; }
    public DateTime? HistoricalCusipBackfillRequestedAt { get; set; }
    public List<string> HistoricalCusipBackfillCandidates { get; set; } = [];
    public DateOnly? HistoricalCusipBackfillCandidateOn { get; set; }
    public bool HistoricalCusipBackfillAmbiguous { get; set; }
    public DateTime? HistoricalCusipBackfillSweepStartedAt { get; set; }

    [MaxLength(2000)]
    public string IdentitySourceUrl { get; set; }
}
