using Microsoft.Extensions.DependencyInjection;

namespace Vestige.Sampling;

/// <summary>Fluent API for configuring the tail-sampling strategy chain.</summary>
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
        _services.AddSingleton<ISamplingStrategy>(new PredicateSampler(predicate));
        return this;
    }

    /// <summary>
    /// Keep events where <c>http.path</c> matches one of <paramref name="patterns"/> (supports * wildcards).
    /// Optionally apply a sample <paramref name="rate"/> (0.0–1.0) instead of always keeping.
    /// </summary>
    public SamplingBuilder AlwaysKeepPaths(double rate = 1.0, params string[] patterns)
    {
        _services.AddSingleton<ISamplingStrategy>(new PathSampler(patterns, rate));
        return this;
    }

    /// <summary>Apply a random sample <paramref name="rate"/> (0.0–1.0) to events matching <paramref name="patterns"/>.</summary>
    public SamplingBuilder RateForPath(double rate, params string[] patterns)
    {
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
