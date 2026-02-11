using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Vestige.Enrichers.Environment;

/// <summary>
/// Enriches events with static host/runtime fields cached at startup:
/// <c>host.name</c>, <c>host.os</c>, <c>runtime.version</c>, <c>process.id</c>.
/// </summary>
public sealed class EnvironmentEnricher : WideEventEnricherBase
{
    private readonly string _hostName = System.Environment.MachineName;
    private readonly string _os = RuntimeInformation.OSDescription;
    private readonly string _runtimeVersion = System.Environment.Version.ToString();
    private readonly int _processId = System.Environment.ProcessId;

    /// <inheritdoc/>
    public override Task OnEventCreatedAsync(WideEvent ev, CancellationToken cancellationToken = default)
    {
        ev.Set("host.name", _hostName)
          .Set("host.os", _os)
          .Set("runtime.version", _runtimeVersion)
          .Set("process.id", _processId);
        return Task.CompletedTask;
    }
}
