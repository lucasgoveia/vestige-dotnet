using Microsoft.Extensions.Hosting;

namespace Vestige.Extensions.Hosting;

/// <summary>
/// Placeholder hosted service for future hosting-specific lifecycle hooks.
/// The pipeline itself is registered as a hosted service by the core package.
/// </summary>
public sealed class VestigePipelineHostedService : IHostedService
{
    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
