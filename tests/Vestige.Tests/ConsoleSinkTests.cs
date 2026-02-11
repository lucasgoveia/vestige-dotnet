using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Vestige;
using Vestige.Internal;
using Vestige.Sinks.Console;
using Vestige.Testing;

namespace Vestige.Tests;

public sealed class ConsoleSinkTests
{
    private static WideEventData MakeData(WideEvent? ev = null)
    {
        ev ??= new WideEvent { ServiceName = "test", Outcome = "success" };
        var serializer = new SystemTextJsonSerializer();
        return serializer.Serialize(ev);
    }

    [Fact]
    public async Task EmitAsync_WritesJson()
    {
        var output = new StringBuilder();
        var originalOut = System.Console.Out;

        using var writer = new StringWriter(output);
        System.Console.SetOut(writer);

        try
        {
            var sink = new ConsoleSink(Options.Create(new ConsoleSinkOptions()));
            await sink.EmitAsync(MakeData());
        }
        finally
        {
            System.Console.SetOut(originalOut);
        }

        var json = output.ToString().Trim();
        Assert.NotEmpty(json);
        var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public async Task EmitAsync_IndentedMode_ProducesIndentedJson()
    {
        var output = new StringBuilder();
        var originalOut = System.Console.Out;

        using var writer = new StringWriter(output);
        System.Console.SetOut(writer);

        try
        {
            var sink = new ConsoleSink(Options.Create(new ConsoleSinkOptions { Indented = true }));
            await sink.EmitAsync(MakeData());
        }
        finally
        {
            System.Console.SetOut(originalOut);
        }

        var json = output.ToString().Trim();
        Assert.Contains('\n', json); // indented output contains newlines
    }

    [Fact]
    public async Task EmitBatchAsync_WritesAllEvents()
    {
        var output = new StringBuilder();
        var originalOut = System.Console.Out;

        using var writer = new StringWriter(output);
        System.Console.SetOut(writer);

        try
        {
            var sink = new ConsoleSink(Options.Create(new ConsoleSinkOptions()));
            var batch = new List<WideEventData>
            {
                MakeData(new WideEvent { Outcome = "success" }),
                MakeData(new WideEvent { Outcome = "error" }),
                MakeData(new WideEvent { Outcome = "success" }),
            };
            await sink.EmitBatchAsync(batch);
        }
        finally
        {
            System.Console.SetOut(originalOut);
        }

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
    }
}
