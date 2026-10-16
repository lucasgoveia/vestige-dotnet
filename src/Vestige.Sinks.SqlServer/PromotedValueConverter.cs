using System.Globalization;

namespace Vestige.Sinks.SqlServer;

internal static class PromotedValueConverter
{
    /// <summary>Convert a non-null field value to the promoted column's CLR type; false when it cannot be represented.</summary>
    public static bool TryConvert(PromotedType type, object value, out object? converted)
    {
        converted = type.Kind switch
        {
            PromotedKind.String => SqlServerEventRow.Truncate(
                value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture),
                type.Length),
            PromotedKind.Guid => value switch
            {
                Guid g => g,
                string s when Guid.TryParse(s, out var parsed) => parsed,
                _ => null,
            },
            PromotedKind.Int => value switch
            {
                int i => i,
                short s => (int)s,
                byte b => (int)b,
                long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
                _ => null,
            },
            PromotedKind.Long => value switch
            {
                long l => l,
                int i => (long)i,
                short s => (long)s,
                byte b => (long)b,
                _ => null,
            },
            PromotedKind.Bool => value as bool?,
            PromotedKind.DateTimeOffset => value switch
            {
                DateTimeOffset dto => dto,
                DateTime dt => new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero),
                string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) => parsed,
                _ => null,
            },
            _ => null,
        };

        return converted is not null;
    }
}
