namespace Vestige.Internal;

/// <summary>Chains a list of <see cref="ISamplingStrategy"/> instances. All-Defer defaults to Keep.</summary>
internal sealed class TailSampler
{
    private readonly ISamplingStrategy[] _strategies;

    public TailSampler(IEnumerable<ISamplingStrategy> strategies)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        _strategies = strategies.ToArray();
    }

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
