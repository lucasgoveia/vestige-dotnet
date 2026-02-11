using Microsoft.Extensions.DependencyInjection;
using Vestige.Sampling;

namespace Vestige;

/// <summary>
/// Fluent builder returned by <see cref="VestigeServiceCollectionExtensions.AddVestige"/>.
/// Used by extension packages to register enrichers, sinks, and pipeline configuration.
/// </summary>
public sealed class VestigeBuilder
{
    internal VestigeBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>The underlying service collection.</summary>
    public IServiceCollection Services { get; }

    /// <summary>Configure the internal pipeline channel options.</summary>
    public VestigeBuilder ConfigurePipeline(Action<PipelineOptions> configure)
    {
        Services.Configure(configure);
        return this;
    }

    /// <summary>Configure the tail-sampling chain.</summary>
    public VestigeBuilder ConfigureSampling(Action<SamplingBuilder> configure)
    {
        var builder = new SamplingBuilder(Services);
        configure(builder);
        return this;
    }

    /// <summary>Register a sink implementation.</summary>
    public VestigeBuilder AddSink<TSink>() where TSink : class, IWideEventSink
    {
        Services.AddSingleton<IWideEventSink, TSink>();
        return this;
    }

    /// <summary>Register a sink instance.</summary>
    public VestigeBuilder AddSink(IWideEventSink sink)
    {
        Services.AddSingleton(sink);
        return this;
    }
}
