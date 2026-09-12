using System.Globalization;
using Vestige;
using Vestige.Sampling;

namespace Vestige.Tests;

public sealed class SamplingStrategyTests
{
    private static WideEvent EventWithPath(string path)
    {
        var ev = new WideEvent();
        ev.Set("http.path", path);
        return ev;
    }

    [Fact]
    public void AlwaysKeepErrors_KeepsErrorOutcome()
        => Assert.Equal(
            SamplingDecision.Keep,
            new AlwaysKeepErrorsSampler().Evaluate(new WideEvent { Outcome = "error" }));

    [Theory]
    [InlineData(500, SamplingDecision.Keep)]
    [InlineData(503, SamplingDecision.Keep)]
    [InlineData(499, SamplingDecision.Defer)]
    [InlineData(200, SamplingDecision.Defer)]
    public void AlwaysKeepErrors_UsesStatusThreshold(int status, SamplingDecision expected)
        => Assert.Equal(
            expected,
            new AlwaysKeepErrorsSampler().Evaluate(new WideEvent { StatusCode = status }));

    [Theory]
    [InlineData(2000, SamplingDecision.Keep)]
    [InlineData(1999, SamplingDecision.Defer)]
    public void SlowRequestSampler_UsesInclusiveThreshold(double duration, SamplingDecision expected)
        => Assert.Equal(
            expected,
            new SlowRequestSampler(2000).Evaluate(new WideEvent { DurationMs = duration }));

    [Fact]
    public void PredicateSampler_DefersWhenPredicateIsFalse()
    {
        var sampler = new PredicateSampler(e => e.Get<string>("tier") == "enterprise");
        Assert.Equal(SamplingDecision.Defer, sampler.Evaluate(new WideEvent()));
    }

    [Fact]
    public void PathSampler_DefersWhenPathFieldIsAbsent()
        => Assert.Equal(SamplingDecision.Defer, new PathSampler(["/x"], 1.0).Evaluate(new WideEvent()));

    [Fact]
    public void PathSampler_DefersWhenNoPatternMatches()
        => Assert.Equal(
            SamplingDecision.Defer,
            new PathSampler(["/healthz"], 1.0).Evaluate(EventWithPath("/orders")));

    [Theory]
    [InlineData("/healthz", "/healthz")]
    [InlineData("/api/*", "/api/orders/42")]
    [InlineData("/v?/ping", "/v1/ping")]
    public void PathSampler_MatchesWildcards(string pattern, string path)
        => Assert.Equal(
            SamplingDecision.Keep,
            new PathSampler([pattern], 1.0).Evaluate(EventWithPath(path)));

    [Fact]
    public void PathSampler_DropsMatchesAtZeroRate()
        => Assert.Equal(
            SamplingDecision.Drop,
            new PathSampler(["/healthz"], 0.0).Evaluate(EventWithPath("/healthz")));

    [Fact]
    public void PathSampler_WildcardIsNotTreatedAsRegex()
    {
        // "." must stay literal, or "/a.b" would also match "/axb".
        var sampler = new PathSampler(["/a.b"], 1.0);
        Assert.Equal(SamplingDecision.Keep, sampler.Evaluate(EventWithPath("/a.b")));
        Assert.Equal(SamplingDecision.Defer, sampler.Evaluate(EventWithPath("/axb")));
    }

    [Fact]
    public void PathSampler_CaseInsensitiveMatchIsCultureInvariant()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // Under the Turkish locale, a culture-sensitive IgnoreCase match fails on "I"/"i".
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            var sampler = new PathSampler(["/Invoices"], 1.0);
            Assert.Equal(SamplingDecision.Keep, sampler.Evaluate(EventWithPath("/invoices")));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(0.0, SamplingDecision.Drop)]
    [InlineData(1.0, SamplingDecision.Keep)]
    public void RateSampler_HonoursTheExtremes(double rate, SamplingDecision expected)
    {
        var sampler = new RateSampler(rate);
        for (int i = 0; i < 200; i++)
            Assert.Equal(expected, sampler.Evaluate(new WideEvent()));
    }

    [Theory]
    [InlineData(-5.0, SamplingDecision.Drop)]
    [InlineData(5.0, SamplingDecision.Keep)]
    public void RateSampler_ClampsOutOfRangeRates(double rate, SamplingDecision expected)
        => Assert.Equal(expected, new RateSampler(rate).Evaluate(new WideEvent()));

    [Fact]
    public void RateSampler_KeepsRoughlyTheConfiguredFraction()
    {
        var sampler = new RateSampler(0.25);
        var kept = Enumerable.Range(0, 20_000)
            .Count(_ => sampler.Evaluate(new WideEvent()) == SamplingDecision.Keep);

        Assert.InRange(kept / 20_000.0, 0.22, 0.28);
    }
}
