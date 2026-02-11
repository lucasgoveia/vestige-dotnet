namespace Vestige.Sampling;

/// <summary>Keeps events where outcome is "error" or HTTP status code >= 500.</summary>
internal sealed class AlwaysKeepErrorsSampler : ISamplingStrategy
{
    public SamplingDecision Evaluate(WideEvent ev)
    {
        if (ev.Outcome == "error")
            return SamplingDecision.Keep;
        if (ev.StatusCode.HasValue && ev.StatusCode.Value >= 500)
            return SamplingDecision.Keep;
        return SamplingDecision.Defer;
    }
}
