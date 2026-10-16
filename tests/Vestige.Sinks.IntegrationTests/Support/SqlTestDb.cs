using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Vestige.Sinks.SqlServer;

namespace Vestige.Sinks.IntegrationTests.Support;

public static class SqlTestDb
{
    public static string UniqueTable() => "we_" + Guid.NewGuid().ToString("N")[..12];

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return (T)value!;
    }

    public static async Task<Dictionary<string, string>> ColumnsAsync(string connectionString, string schema, string table)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT c.name, t.name + CASE WHEN c.is_nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END " +
            "FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id " +
            "WHERE c.object_id = OBJECT_ID(@name, N'U')",
            connection);
        command.Parameters.AddWithValue("@name", $"[{schema}].[{table}]");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    public static async Task<HashSet<string>> IndexNamesAsync(string connectionString, string schema, string table)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT name FROM sys.indexes WHERE object_id = OBJECT_ID(@name, N'U') AND name IS NOT NULL", connection);
        command.Parameters.AddWithValue("@name", $"[{schema}].[{table}]");
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(reader.GetString(0));
        return result;
    }
}

public static class SinkFactory
{
    public static SqlServerSink SqlServer(
        string connectionString,
        string table,
        Action<SqlServerSinkOptions>? configure = null,
        ListLogger<SqlServerSink>? logger = null)
    {
        var options = new SqlServerSinkOptions { ConnectionString = connectionString, Schema = "dbo", Table = table };
        configure?.Invoke(options);
        return new SqlServerSink(Options.Create(options), logger ?? new ListLogger<SqlServerSink>());
    }
}
