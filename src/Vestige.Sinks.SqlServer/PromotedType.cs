using System.Data;

namespace Vestige.Sinks.SqlServer;

internal enum PromotedKind
{
    String,
    Guid,
    Int,
    Long,
    Bool,
    DateTimeOffset,
}

/// <summary>SQL column type for a promoted field.</summary>
public sealed class PromotedType
{
    private PromotedType(PromotedKind kind, int length)
    {
        Kind = kind;
        Length = length;
    }

    /// <summary><c>NVARCHAR(length)</c>; 1–4000 so the column stays indexable.</summary>
    public static PromotedType String(int length)
    {
        if (length is < 1 or > 4000)
            throw new ArgumentOutOfRangeException(nameof(length), length, "Promoted string length must be between 1 and 4000.");
        return new PromotedType(PromotedKind.String, length);
    }

    /// <summary><c>UNIQUEIDENTIFIER</c>.</summary>
    public static PromotedType Guid { get; } = new(PromotedKind.Guid, 0);

    /// <summary><c>INT</c>.</summary>
    public static PromotedType Int { get; } = new(PromotedKind.Int, 0);

    /// <summary><c>BIGINT</c>.</summary>
    public static PromotedType Long { get; } = new(PromotedKind.Long, 0);

    /// <summary><c>BIT</c>.</summary>
    public static PromotedType Bool { get; } = new(PromotedKind.Bool, 0);

    /// <summary><c>DATETIMEOFFSET(7)</c>.</summary>
    public static PromotedType DateTimeOffset { get; } = new(PromotedKind.DateTimeOffset, 0);

    internal PromotedKind Kind { get; }

    internal int Length { get; }

    internal string SqlTypeName => Kind switch
    {
        PromotedKind.String => $"NVARCHAR({Length})",
        PromotedKind.Guid => "UNIQUEIDENTIFIER",
        PromotedKind.Int => "INT",
        PromotedKind.Long => "BIGINT",
        PromotedKind.Bool => "BIT",
        PromotedKind.DateTimeOffset => "DATETIMEOFFSET(7)",
        _ => throw new InvalidOperationException($"Unknown promoted kind {Kind}."),
    };

    internal SqlDbType SqlDbType => Kind switch
    {
        PromotedKind.String => SqlDbType.NVarChar,
        PromotedKind.Guid => SqlDbType.UniqueIdentifier,
        PromotedKind.Int => SqlDbType.Int,
        PromotedKind.Long => SqlDbType.BigInt,
        PromotedKind.Bool => SqlDbType.Bit,
        PromotedKind.DateTimeOffset => SqlDbType.DateTimeOffset,
        _ => throw new InvalidOperationException($"Unknown promoted kind {Kind}."),
    };

    internal Type ClrType => Kind switch
    {
        PromotedKind.String => typeof(string),
        PromotedKind.Guid => typeof(System.Guid),
        PromotedKind.Int => typeof(int),
        PromotedKind.Long => typeof(long),
        PromotedKind.Bool => typeof(bool),
        PromotedKind.DateTimeOffset => typeof(System.DateTimeOffset),
        _ => throw new InvalidOperationException($"Unknown promoted kind {Kind}."),
    };

    /// <summary>Compare against <c>sys.types.name</c>, <c>sys.columns.max_length</c> (bytes) and <c>scale</c>.</summary>
    internal bool Matches(string typeName, short maxLength, byte scale) => Kind switch
    {
        PromotedKind.String => typeName == "nvarchar" && maxLength == Length * 2,
        PromotedKind.Guid => typeName == "uniqueidentifier",
        PromotedKind.Int => typeName == "int",
        PromotedKind.Long => typeName == "bigint",
        PromotedKind.Bool => typeName == "bit",
        PromotedKind.DateTimeOffset => typeName == "datetimeoffset" && scale == 7,
        _ => false,
    };

    public override string ToString() => SqlTypeName;
}
