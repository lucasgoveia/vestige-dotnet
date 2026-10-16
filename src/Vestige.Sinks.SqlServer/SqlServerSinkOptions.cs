using Vestige.Retention;

namespace Vestige.Sinks.SqlServer;

public sealed class SqlServerSinkOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public string Schema { get; set; } = "dbo";
    public string Table { get; set; } = "wide_events";
    public bool AutoCreate { get; set; } = true;

    /// <summary>Fields stored in their own typed columns so they can be indexed and filtered cheaply.</summary>
    public PromotedFieldCollection PromotedFields { get; } = new();

    /// <summary>Tiered retention. Off until <see cref="RetentionOptions.Default"/> is set.</summary>
    public RetentionOptions Retention { get; } = new();
}
