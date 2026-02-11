namespace Vestige;

/// <summary>Tail-sampling strategy evaluated after a request completes.</summary>
public interface ISamplingStrategy
{
    /// <summary>
    /// Evaluate whether to keep or drop <paramref name="ev"/>.
    /// Return <see cref="SamplingDecision.Defer"/> to pass to the next strategy.
    /// </summary>
    SamplingDecision Evaluate(WideEvent ev);
}
