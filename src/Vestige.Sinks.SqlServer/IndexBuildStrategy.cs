namespace Vestige.Sinks.SqlServer;

internal enum IndexBuildMode
{
    Online,
    Skip,
}

internal static class IndexBuildStrategy
{
    /// <summary>
    /// <c>SERVERPROPERTY('EngineEdition')</c>: 3 Enterprise/Developer, 5 Azure SQL Database,
    /// 8 Managed Instance support <c>ONLINE = ON</c>; anything else would block inserts.
    /// </summary>
    public static IndexBuildMode ForEngineEdition(int engineEdition)
        => engineEdition is 3 or 5 or 8 ? IndexBuildMode.Online : IndexBuildMode.Skip;
}
