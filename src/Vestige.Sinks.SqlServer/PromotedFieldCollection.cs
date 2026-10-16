using System.Collections;
using System.Text;

namespace Vestige.Sinks.SqlServer;

/// <summary>Fields the SQL Server sink stores in typed, optionally indexed columns.</summary>
public sealed class PromotedFieldCollection : IReadOnlyList<PromotedField>
{
    private readonly List<PromotedField> _fields = [];

    public int Count => _fields.Count;

    public PromotedField this[int index] => _fields[index];

    /// <summary>Promote <paramref name="key"/>. The column defaults to the key lower-cased with non <c>[a-z0-9_]</c> characters replaced by <c>_</c>.</summary>
    public PromotedFieldCollection Add(string key, PromotedType type, string? column = null, bool indexed = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(type);

        _fields.Add(new PromotedField(key, type, column ?? DefaultColumnName(key), indexed));
        return this;
    }

    public IEnumerator<PromotedField> GetEnumerator() => _fields.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal static string DefaultColumnName(string key)
    {
        var builder = new StringBuilder(key.Length);
        foreach (var c in key.ToLowerInvariant())
            builder.Append(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' ? c : '_');
        return builder.ToString();
    }
}
