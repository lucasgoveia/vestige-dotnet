using Microsoft.Extensions.DependencyInjection;

namespace Vestige.Sampling;

/// <summary>Fluent API for configuring the tail-sampling strategy chain.</summary>
/// <remarks>
/// Strategies are evaluated in registration order. The first non-<see cref="SamplingDecision.Defer"/>
/// decision wins; if every strategy defers, the event is kept. Register
/// <see cref="DefaultRate"/> last as the catch-all.
/// </remarks>
public sealed class SamplingBuilder
{
    private readonly IServiceCollection _services;

    internal SamplingBuilder(IServiceCollection services)
    {
        _services = services;
    }

    /// <summary>Always keep events where outcome is "error" or HTTP status >= 500.</summary>
    public SamplingBuilder AlwaysKeepErrors()
    {
        _services.AddSingleton<ISamplingStrategy, AlwaysKeepErrorsSampler>();
        return this;
    }

    /// <summary>Always keep events where <see cref="WideEvent.DurationMs"/> >= <paramref name="thresholdMs"/>.</summary>
    public SamplingBuilder AlwaysKeepSlowRequests(double thresholdMs)
    {
        _services.AddSingleton<ISamplingStrategy>(new SlowRequestSampler(thresholdMs));
        return this;
    }

    /// <summary>Always keep events matching <paramref name="predicate"/>.</summary>
    public SamplingBuilder AlwaysKeepWhen(Func<WideEvent, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _services.AddSingleton<ISamplingStrategy>(new PredicateSampler(predicate));
        return this;
    }

    /// <summary>
    /// Always keep events whose <c>http.path</c> matches one of <paramref name="patterns"/>
    /// (<c>*</c> matches any run of characters, <c>?</c> matches one).
    /// </summary>
    /// <example><c>sampling.AlwaysKeepPaths("/checkout/*", "/admin/*");</c></example>
    public SamplingBuilder AlwaysKeepPaths(params string[] patterns)
        => RateForPath(1.0, patterns);

    /// <summary>
    /// Keep a random fraction of events whose <c>http.path</c> matches one of
    /// <paramref name="patterns"/>. Matching events that lose the draw are dropped outright — they
    /// do not fall through to later strategies.
    /// </summary>
    /// <param name="rate">Fraction to keep, 0.0–1.0.</param>
    /// <param name="patterns">Wildcard path patterns.</param>
    /// <example><c>sampling.RateForPath(0.001, "/healthz");</c></example>
    public SamplingBuilder RateForPath(double rate, params string[] patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        if (patterns.Length == 0)
            throw new ArgumentException("At least one path pattern is required.", nameof(patterns));

        _services.AddSingleton<ISamplingStrategy>(new PathSampler(patterns, rate));
        return this;
    }

    /// <summary>
    /// Terminal catch-all: keep a random fraction of remaining events.
    /// <paramref name="rate"/> 1.0 = keep all, 0.0 = drop all.
    /// </summary>
    public SamplingBuilder DefaultRate(double rate)
    {
        _services.AddSingleton<ISamplingStrategy>(new RateSampler(rate));
        return this;
    }
}
