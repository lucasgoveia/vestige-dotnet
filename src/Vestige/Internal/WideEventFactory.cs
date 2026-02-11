using Microsoft.Extensions.Options;

namespace Vestige.Internal;

/// <summary>Creates <see cref="WideEvent"/> instances from configured options.</summary>
internal sealed class WideEventFactory(IOptions<VestigeOptions> options) : IWideEventFactory
{
    private readonly VestigeOptions _options = options.Value;

    /// <inheritdoc/>
    public WideEvent Create()
    {
        var ev = new WideEvent
        {
            ServiceName = _options.ServiceName,
        };

        if (_options.ServiceVersion is not null)
            ev.Set("service.version", _options.ServiceVersion);
        if (_options.Region is not null)
            ev.Set("cloud.region", _options.Region);
        if (_options.DeploymentId is not null)
            ev.Set("deployment.id", _options.DeploymentId);
        if (_options.Environment is not null)
            ev.Set("deployment.environment", _options.Environment);

        return ev;
    }
}
