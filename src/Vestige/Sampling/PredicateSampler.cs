namespace Vestige.Sampling;

/// <summary>Keeps events that match a user-supplied predicate.</summary>
internal sealed class PredicateSampler(Func<WideEvent, bool> predicate) : ISamplingStrategy
{
    public SamplingDecision Evaluate(WideEvent ev)
        => predicate(ev) ? SamplingDecision.Keep : SamplingDecision.Defer;
}
