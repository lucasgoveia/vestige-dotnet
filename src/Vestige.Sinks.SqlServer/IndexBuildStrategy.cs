namespace Vestige.Sinks.SqlServer;

internal enum IndexBuildMode
{
    /// <summary><c>ONLINE = ON</c> with <c>WAIT_AT_LOW_PRIORITY</c>: writers never queue behind the build.</summary>
    OnlineLowPriority,

    /// <summary><c>ONLINE = ON</c> with a bounded lock wait.</summary>
    Online,

    /// <summary>No online builds on this edition; an offline build would block inserts.</summary>
    Skip,
}

internal static class IndexBuildStrategy
{
    /// <summary>
    /// <c>SERVERPROPERTY('EngineEdition')</c>: 5 Azure SQL Database and 8 Managed Instance support low-priority
    /// online builds; 3 Enterprise/Developer supports online builds, at low priority from SQL Server 2022 (16).
    /// Anything else would block inserts.
    /// </summary>
    public static IndexBuildMode ForServer(int engineEdition, int productMajorVersion) => engineEdition switch
    {
        5 or 8 => IndexBuildMode.OnlineLowPriority,
        3 when productMajorVersion >= 16 => IndexBuildMode.OnlineLowPriority,
        3 => IndexBuildMode.Online,
        _ => IndexBuildMode.Skip,
    };
}
