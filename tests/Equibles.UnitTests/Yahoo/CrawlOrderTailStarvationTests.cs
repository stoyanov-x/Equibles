using Equibles.Yahoo.HostedService.Services;
using FluentAssertions;
using Xunit;

namespace Equibles.UnitTests.Yahoo;

// A listing that stops trading falls out of the actively-traded window. Sorting it last meant the
// rotation only reached it after walking every active listing, and because each cycle rebuilds the
// order from the top it was never reached at all: DSGX sat unpriced for 33 days with no attempt
// logged. The dormant tail is now interleaved instead.
public class CrawlOrderTailStarvationTests
{
    private static readonly DateOnly Today = new(2026, 11, 5);

    private static PriceSeriesTarget Target(string ticker) =>
        new(ticker, Guid.NewGuid(), Guid.NewGuid(), IsPrimary: true);

    [Fact]
    public void DormantListingIsReachedInsideOneCycleInsteadOfAfterEveryActiveOne()
    {
        var targets = new List<PriceSeriesTarget>();
        var lastDates = new Dictionary<Guid, DateOnly>();
        for (var index = 0; index < 40; index++)
        {
            var active = Target($"ACT{index:00}");
            targets.Add(active);
            lastDates[active.EquityListingId] = Today.AddDays(-1);
        }
        var dormant = Target("DSGX");
        targets.Add(dormant);
        lastDates[dormant.EquityListingId] = Today.AddDays(-34);

        var ordered = YahooPriceImportService.BuildCrawlOrder(targets, lastDates, Today);

        ordered.Select(target => target.Ticker).ToList().IndexOf("DSGX").Should().BeLessThan(20);
    }

    [Fact]
    public void ActivelyTradedListingsKeepTheirRelativeOrder()
    {
        var targets = new List<PriceSeriesTarget>();
        var lastDates = new Dictionary<Guid, DateOnly>();
        foreach (var ticker in new[] { "CCC", "AAA", "BBB" })
        {
            var active = Target(ticker);
            targets.Add(active);
            lastDates[active.EquityListingId] = Today.AddDays(-1);
        }

        var ordered = YahooPriceImportService.BuildCrawlOrder(targets, lastDates, Today);

        ordered.Select(target => target.Ticker).Should().Equal("AAA", "BBB", "CCC");
    }
}
