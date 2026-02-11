namespace Vestige;

/// <summary>Top-level configuration for the Vestige library.</summary>
public sealed class VestigeOptions
{
    /// <summary>Logical service name stamped on every event.</summary>
    public string? ServiceName { get; set; }

    /// <summary>Service version (SemVer recommended).</summary>
    public string? ServiceVersion { get; set; }

    /// <summary>Cloud/on-prem region (e.g. "eastus", "eu-west-1").</summary>
    public string? Region { get; set; }

    /// <summary>Deployment identifier (e.g. container id, slot name).</summary>
    public string? DeploymentId { get; set; }

    /// <summary>Runtime environment name (e.g. "production", "staging").</summary>
    public string? Environment { get; set; }
}
