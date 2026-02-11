using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Vestige.Sinks.Console;

/// <summary>Writes wide events as JSON lines to stdout.</summary>
public sealed class ConsoleSink : IWideEventSink
{
    private static readonly JsonSerializerOptions s_indented = new() { WriteIndented = true };
    private static readonly byte[] s_newline = [(byte)'\n'];
    private static readonly Stream s_stdout = System.Console.OpenStandardOutput();
    private readonly ConsoleSinkOptions _options;

    public ConsoleSink(IOptions<ConsoleSinkOptions> options)
    {
        _options = options.Value;
    }

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task EmitAsync(WideEventData data, CancellationToken cancellationToken = default)
    {
        WriteData(data);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task EmitBatchAsync(IReadOnlyList<WideEventData> batch, CancellationToken cancellationToken = default)
    {
        foreach (var data in batch)
            WriteData(data);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void WriteData(WideEventData data)
    {
        if (_options.Indented)
        {
            string json = JsonSerializer.Serialize(data.Fields, s_indented);
            System.Console.WriteLine(json);
            return;
        }

        s_stdout.Write(data.JsonBytes, 0, data.JsonBytes.Length);
        s_stdout.Write(s_newline, 0, s_newline.Length);
    }
}
