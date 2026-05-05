namespace Vestige.Sinks.SqlServer;

public sealed class SqlServerSinkOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public string Schema { get; set; } = "dbo";
    public string Table { get; set; } = "wide_events";
    public bool AutoCreate { get; set; } = true;
}
