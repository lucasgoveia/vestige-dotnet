namespace Vestige.Internal;

/// <summary>Chains a list of <see cref="ISamplingStrategy"/> instances. All-Defer defaults to Keep.</summary>
internal sealed class TailSampler(IEnumerable<ISamplingStrategy> strategies)
{
    private readonly IReadOnlyList<ISamplingStrategy> _strategies = strategies.ToList();

    public SamplingDecision Evaluate(WideEvent ev)
    {
        foreach (var strategy in _strategies)
        {
            var decision = strategy.Evaluate(ev);
            if (decision != SamplingDecision.Defer)
                return decision;
        }
        return SamplingDecision.Keep; // all-Defer → Keep
    }
}
