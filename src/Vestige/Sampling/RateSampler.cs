namespace Vestige.Sampling;

/// <summary>Terminal strategy: keep a random fraction of all remaining events.</summary>
internal sealed class RateSampler(double rate) : ISamplingStrategy
{
    private static readonly ThreadLocal<Random> s_random = new(() => new Random());
    private readonly double _rate = Math.Clamp(rate, 0.0, 1.0);

    public SamplingDecision Evaluate(WideEvent ev)
        => s_random.Value!.NextDouble() < _rate ? SamplingDecision.Keep : SamplingDecision.Drop;
}
