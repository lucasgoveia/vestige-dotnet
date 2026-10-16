using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Vestige.Sinks.SqlServer;

/// <summary>Logs a failed promotion at most once per field per minute. Never logs the value itself.</summary>
internal sealed class PromotionWarningLimiter(ILogger logger, TimeProvider timeProvider)
{
    private static readonly long WindowTicks = TimeSpan.FromMinutes(1).Ticks;
    private readonly ConcurrentDictionary<string, long> _lastWarnedTicks = new(StringComparer.Ordinal);

    public void Report(PromotedField field, object value)
    {
        var now = timeProvider.GetUtcNow().UtcTicks;
        var last = _lastWarnedTicks.GetOrAdd(field.Key, long.MinValue);
        if (last != long.MinValue && now - last < WindowTicks)
            return;
        if (!_lastWarnedTicks.TryUpdate(field.Key, now, last))
            return;

        logger.LogWarning(
            "Vestige could not convert field '{Field}' of type {ValueType} to promoted column [{Column}] {SqlType}; wrote NULL. The value is still in payload.",
            field.Key,
            value.GetType().Name,
            field.Column,
            field.Type.SqlTypeName);
    }
}
