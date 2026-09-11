using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Vestige;
using Vestige.Internal;
using Vestige.Sinks.Console;

namespace Vestige.Tests;

public sealed class ConsoleSinkTests
{
    private static WideEventData MakeData(WideEvent? ev = null)
    {
        ev ??= new WideEvent { ServiceName = "test", Outcome = "success" };
        var serializer = new SystemTextJsonSerializer();
        return serializer.Serialize(ev);
    }

    /// <summary>
    /// The sink writes to the raw stdout stream, so tests capture that stream directly rather than
    /// swapping <c>Console.Out</c> — which the sink deliberately bypasses.
    /// </summary>
    private static (ConsoleSink Sink, MemoryStream Output) CreateSink(bool indented = false)
    {
        var output = new MemoryStream();
        var sink = new ConsoleSink(Options.Create(new ConsoleSinkOptions { Indented = indented }), output);
        return (sink, output);
    }

    private static string ReadText(MemoryStream output) => Encoding.UTF8.GetString(output.ToArray());

    [Fact]
    public async Task EmitAsync_WritesJson()
    {
        var (sink, output) = CreateSink();
        await using (sink)
        {
            await sink.EmitAsync(MakeData(), TestContext.Current.CancellationToken);
        }

        var json = ReadText(output).Trim();
        Assert.NotEmpty(json);
        var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public async Task EmitAsync_IndentedMode_ProducesIndentedJson()
    {
        var (sink, output) = CreateSink(indented: true);
        await using (sink)
        {
            await sink.EmitAsync(MakeData(), TestContext.Current.CancellationToken);
        }

        var json = ReadText(output).Trim();
        Assert.Contains('\n', json); // indented output contains newlines
        Assert.Equal(JsonValueKind.Object, JsonDocument.Parse(json).RootElement.ValueKind);
    }

    [Fact]
    public async Task EmitBatchAsync_WritesAllEvents()
    {
        var (sink, output) = CreateSink();
        await using (sink)
        {
            var batch = new List<WideEventData>
            {
                MakeData(new WideEvent { Outcome = "success" }),
                MakeData(new WideEvent { Outcome = "error" }),
                MakeData(new WideEvent { Outcome = "success" }),
            };
            await sink.EmitBatchAsync(batch, TestContext.Current.CancellationToken);
        }

        var lines = ReadText(output).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.All(lines, line => Assert.Equal(JsonValueKind.Object, JsonDocument.Parse(line).RootElement.ValueKind));
    }

    [Fact]
    public async Task EmitBatchAsync_EventLargerThanBuffer_StillWritten()
    {
        var output = new MemoryStream();
        var options = Options.Create(new ConsoleSinkOptions { BufferSizeBytes = 1 }); // clamped up to 4096
        var sink = new ConsoleSink(options, output);

        var ev = new WideEvent();
        ev.Set("big", new string('x', 4096)); // MaxValueLength, so it survives truncation intact

        await using (sink)
        {
            await sink.EmitBatchAsync([MakeData(ev)], TestContext.Current.CancellationToken);
        }

        var json = ReadText(output).Trim();
        var doc = JsonDocument.Parse(json);
        Assert.Equal(4096, doc.RootElement.GetProperty("big").GetString()!.Length);
    }

    [Fact]
    public async Task EmitBatchAsync_EmptyBatch_WritesNothing()
    {
        var (sink, output) = CreateSink();
        await using (sink)
        {
            await sink.EmitBatchAsync([], TestContext.Current.CancellationToken);
        }

        Assert.Empty(output.ToArray());
    }
}
