using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Vestige.Sinks.Console;

/// <summary>Writes wide events as JSON lines to stdout.</summary>
/// <remarks>
/// Writes the pre-serialized UTF-8 bytes straight to the standard output stream through a buffer
/// that is flushed once per batch, rather than decoding to a <see cref="string"/> and re-encoding
/// through <c>Console.WriteLine</c> — which locks and flushes on every single event.
/// </remarks>
public sealed class ConsoleSink : IWideEventSink
{
    private static readonly JsonSerializerOptions s_indented = new() { WriteIndented = true };
    private static readonly byte[] s_newLine = "\n"u8.ToArray();

    private readonly ConsoleSinkOptions _options;
    private readonly Stream _output;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly byte[] _buffer;
    private int _buffered;
    private bool _disposed;

    public ConsoleSink(IOptions<ConsoleSinkOptions> options)
        : this(options, System.Console.OpenStandardOutput())
    {
    }

    internal ConsoleSink(IOptions<ConsoleSinkOptions> options, Stream output)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        _options = options.Value;
        _output = output;
        _buffer = new byte[Math.Max(4096, _options.BufferSizeBytes)];
    }

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public async Task EmitAsync(WideEventData data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await FlushBufferAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task EmitBatchAsync(IReadOnlyList<WideEventData> batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
            return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var data in batch)
                await WriteAsync(data, cancellationToken).ConfigureAwait(false);

            await FlushBufferAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FlushBufferAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        try { await FlushAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { /* best-effort */ }

        _gate.Dispose();

        // The standard output stream is process-owned; closing it would silence every other writer.
        await _output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Must be called while holding <see cref="_gate"/>.</summary>
    private async ValueTask WriteAsync(WideEventData data, CancellationToken cancellationToken)
    {
        if (_options.Indented)
        {
            // The indented form is a development affordance; it re-serializes by design.
            var text = JsonSerializer.Serialize(data.Fields, s_indented);
            await AppendAsync(System.Text.Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await AppendAsync(data.JsonBytes, cancellationToken).ConfigureAwait(false);
        }

        await AppendAsync(s_newLine, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AppendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        // Anything that cannot fit the buffer bypasses it entirely rather than forcing a grow.
        if (bytes.Length >= _buffer.Length)
        {
            await FlushBufferAsync(cancellationToken).ConfigureAwait(false);
            await _output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_buffered + bytes.Length > _buffer.Length)
            await FlushBufferAsync(cancellationToken).ConfigureAwait(false);

        bytes.Span.CopyTo(_buffer.AsSpan(_buffered));
        _buffered += bytes.Length;
    }

    private async ValueTask FlushBufferAsync(CancellationToken cancellationToken)
    {
        if (_buffered == 0)
            return;

        var pending = _buffered;
        _buffered = 0;
        await _output.WriteAsync(_buffer.AsMemory(0, pending), cancellationToken).ConfigureAwait(false);
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
