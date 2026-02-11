namespace Vestige.Sampling;

/// <summary>Keeps events where <see cref="WideEvent.DurationMs"/> exceeds a threshold.</summary>
internal sealed class SlowRequestSampler(double thresholdMs) : ISamplingStrategy
{
    public SamplingDecision Evaluate(WideEvent ev)
        => ev.DurationMs >= thresholdMs ? SamplingDecision.Keep : SamplingDecision.Defer;
}
