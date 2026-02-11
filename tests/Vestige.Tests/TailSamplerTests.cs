using Vestige;
using Vestige.Internal;
using Vestige.Sampling;

namespace Vestige.Tests;

public sealed class TailSamplerTests
{
    [Fact]
    public void EmptyChain_DefaultsToKeep()
    {
        var sampler = new TailSampler([]);
        var ev = new WideEvent();
        Assert.Equal(SamplingDecision.Keep, sampler.Evaluate(ev));
    }

    [Fact]
    public void AlwaysKeepErrors_KeepsErrorOutcome()
    {
        var sampler = new TailSampler([new AlwaysKeepErrorsSampler()]);
        var ev = new WideEvent { Outcome = "error" };
        Assert.Equal(SamplingDecision.Keep, sampler.Evaluate(ev));
    }

    [Fact]
    public void AlwaysKeepErrors_KeepsHighStatusCode()
    {
        var sampler = new TailSampler([new AlwaysKeepErrorsSampler()]);
        var ev = new WideEvent { StatusCode = 503 };
        Assert.Equal(SamplingDecision.Keep, sampler.Evaluate(ev));
    }

    [Fact]
    public void AlwaysKeepErrors_DefersSuccessEvent()
    {
        var sampler = new TailSampler([new AlwaysKeepErrorsSampler()]);
        var ev = new WideEvent { Outcome = "success", StatusCode = 200 };
        // single-strategy chain: Defer → Keep (all-Defer default)
        Assert.Equal(SamplingDecision.Keep, sampler.Evaluate(ev));
    }

    [Fact]
    public void SlowRequestSampler_KeepsSlowRequest()
    {
        var sampler = new TailSampler([new SlowRequestSampler(1000)]);
        var ev = new WideEvent { DurationMs = 1500 };
        Assert.Equal(SamplingDecision.Keep, sampler.Evaluate(ev));
    }

    [Fact]
    public void SlowRequestSampler_DefersFastRequest()
    {
        var sampler = new TailSampler([new SlowRequestSampler(1000)]);
        var ev = new WideEvent { DurationMs = 50 };
        Assert.Equal(SamplingDecision.Keep, sampler.Evaluate(ev)); // Defer → Keep
    }

    [Fact]
    public void PredicateSampler_KeepsWhenPredicateTrue()
    {
        var sampler = new TailSampler([new PredicateSampler(e => e.Get<string>("user.tier") == "enterprise")]);
        var ev = new WideEvent();
        ev.Set("user.tier", "enterprise");
        Assert.Equal(SamplingDecision.Keep, sampler.Evaluate(ev));
    }

    [Fact]
    public void PredicateSampler_DeferWhenPredicateFalse()
    {
        var sampler = new TailSampler([new PredicateSampler(e => false)]);
        var ev = new WideEvent();
        // Defer → Keep
        Assert.Equal(SamplingDecision.Keep, sampler.Evaluate(ev));
    }

    [Fact]
    public void ChainPriority_FirstKeepWins()
    {
        // Error sampler fires first (Keep), rate sampler would drop — Keep should win
        var sampler = new TailSampler([
            new AlwaysKeepErrorsSampler(),
            new RateSampler(0.0), // would drop everything
        ]);
        var ev = new WideEvent { Outcome = "error" };
        Assert.Equal(SamplingDecision.Keep, sampler.Evaluate(ev));
    }

    [Fact]
    public void RateSampler_DropAll_DropsEvent()
    {
        // Rate 0.0 always drops
        var sampler = new TailSampler([new RateSampler(0.0)]);
        var ev = new WideEvent();
        Assert.Equal(SamplingDecision.Drop, sampler.Evaluate(ev));
    }

    [Fact]
    public void RateSampler_KeepAll_KeepsEvent()
    {
        var sampler = new TailSampler([new RateSampler(1.0)]);
        var ev = new WideEvent();
        Assert.Equal(SamplingDecision.Keep, sampler.Evaluate(ev));
    }
}
