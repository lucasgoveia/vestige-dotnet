using Vestige.Sinks.SqlServer;

namespace Vestige.Tests;

public sealed class PromotedValueConverterTests
{
    private static readonly Guid SampleGuid = Guid.Parse("6f1c2a5e-1d2b-4c3d-9e8f-0a1b2c3d4e5f");

    public static TheoryData<PromotedType, object, object?> Convertible => new()
    {
        { PromotedType.String(32), "tenant.create", "tenant.create" },
        { PromotedType.String(5), "abcdefgh", "abcde" },
        { PromotedType.String(2), "a\U0001F600", "a" },
        { PromotedType.String(10), 42, "42" },
        { PromotedType.String(10), 1.5, "1.5" },
        { PromotedType.Guid, SampleGuid, SampleGuid },
        { PromotedType.Guid, SampleGuid.ToString(), SampleGuid },
        { PromotedType.Guid, "{" + SampleGuid + "}", SampleGuid },
        { PromotedType.Int, 7, 7 },
        { PromotedType.Int, (short)7, 7 },
        { PromotedType.Int, (byte)7, 7 },
        { PromotedType.Int, 7L, 7 },
        { PromotedType.Long, 7, 7L },
        { PromotedType.Long, 7L, 7L },
        { PromotedType.Bool, true, true },
        { PromotedType.DateTimeOffset, new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero) },
        { PromotedType.DateTimeOffset, "2026-10-16T12:00:00.0000000+00:00", new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero) },
        { PromotedType.DateTimeOffset, new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc), new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero) },
    };

    public static TheoryData<PromotedType, object> NotConvertible => new()
    {
        { PromotedType.Guid, "not-a-guid" },
        { PromotedType.Guid, 12 },
        { PromotedType.Int, (long)int.MaxValue + 1 },
        { PromotedType.Int, "7" },
        { PromotedType.Long, 1.5 },
        { PromotedType.Bool, "true" },
        { PromotedType.DateTimeOffset, "yesterday" },
    };

    [Theory]
    [MemberData(nameof(Convertible))]
    public void TryConvert_SupportedValues(PromotedType type, object value, object? expected)
    {
        Assert.True(PromotedValueConverter.TryConvert(type, value, out var converted));
        Assert.Equal(expected, converted);
    }

    [Theory]
    [MemberData(nameof(NotConvertible))]
    public void TryConvert_UnsupportedValues_ReturnsFalse(PromotedType type, object value)
    {
        Assert.False(PromotedValueConverter.TryConvert(type, value, out var converted));
        Assert.Null(converted);
    }
}
