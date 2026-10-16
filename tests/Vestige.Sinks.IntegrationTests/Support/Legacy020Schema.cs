namespace Vestige.Sinks.IntegrationTests.Support;

/// <summary>The literal 0.2.0 DDL, copied here so upgrade tests start from what production really has.</summary>
public static class Legacy020Schema
{
    public static Task CreateAsync(string connectionString, string schema, string table)
    {
        const string payload = "{\"audit_action\":\"tenant.create\"}";
        var prefix = $"{schema}_{table}";
        return SqlTestDb.ExecuteAsync(connectionString, $"""
            CREATE TABLE [{schema}].[{table}] (
                [id] BIGINT IDENTITY(1,1) PRIMARY KEY,
                [event_id] VARCHAR(256) NOT NULL,
                [timestamp_utc] DATETIMEOFFSET(7) NOT NULL,
                [service_name] NVARCHAR(256) NULL,
                [service_version] NVARCHAR(64) NULL,
                [deployment_environment] NVARCHAR(128) NULL,
                [cloud_region] NVARCHAR(128) NULL,
                [trace_id] NVARCHAR(64) NULL,
                [span_id] NVARCHAR(32) NULL,
                [outcome] NVARCHAR(32) NULL,
                [duration_ms] FLOAT NOT NULL,
                [http_status_code] INT NULL,
                [http_method] NVARCHAR(16) NULL,
                [http_path] NVARCHAR(2048) NULL,
                [error_type] NVARCHAR(512) NULL,
                [payload] NVARCHAR(MAX) NOT NULL
            );
            CREATE INDEX [{prefix}_event_id_idx] ON [{schema}].[{table}] ([event_id]);
            CREATE INDEX [{prefix}_timestamp_idx] ON [{schema}].[{table}] ([timestamp_utc]);
            CREATE INDEX [{prefix}_service_name_idx] ON [{schema}].[{table}] ([service_name]);
            CREATE INDEX [{prefix}_outcome_idx] ON [{schema}].[{table}] ([outcome]);
            CREATE INDEX [{prefix}_trace_id_idx] ON [{schema}].[{table}] ([trace_id]);
            INSERT INTO [{schema}].[{table}] (event_id, timestamp_utc, duration_ms, payload)
            VALUES ('legacy-1', SYSDATETIMEOFFSET(), 1, N'{payload}');
            """);
    }
}
