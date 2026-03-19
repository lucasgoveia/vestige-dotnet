namespace Vestige.Sinks.Postgres;

/// <summary>Configuration for the PostgreSQL sink.</summary>
public sealed class PostgresSinkOptions
{
    /// <summary>Connection string used to connect to PostgreSQL.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Schema name containing the events table.</summary>
    public string Schema { get; set; } = "vestige";

    /// <summary>Table name used to persist wide events.</summary>
    public string Table { get; set; } = "wide_events";

    /// <summary>Create schema, table, and indexes automatically when missing.</summary>
    public bool AutoCreate { get; set; } = true;

}
