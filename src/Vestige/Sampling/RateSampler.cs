namespace Vestige.Sampling;

/// <summary>Terminal strategy: keep a random fraction of all remaining events.</summary>
internal sealed class RateSampler(double rate) : ISamplingStrategy
{
    private readonly double _rate = Math.Clamp(rate, 0.0, 1.0);

    public SamplingDecision Evaluate(WideEvent ev)
        => Random.Shared.NextDouble() < _rate ? SamplingDecision.Keep : SamplingDecision.Drop;
}
