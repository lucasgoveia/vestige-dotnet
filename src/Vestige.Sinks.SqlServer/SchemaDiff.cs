namespace Vestige.Sinks.SqlServer;

/// <summary>A row of <c>sys.columns</c> joined to <c>sys.types</c>.</summary>
internal sealed record ExistingColumn(string Name, string TypeName, short MaxLength, byte Scale, bool IsNullable);

/// <summary>What the existing table lacks for the configured promoted fields.</summary>
internal sealed record SchemaDiff(IReadOnlyList<PromotedField> MissingColumns, IReadOnlyList<PromotedField> MissingIndexes)
{
    public static SchemaDiff Compute(
        SqlServerTableLayout layout,
        IReadOnlyList<ExistingColumn> columns,
        IReadOnlySet<string> indexNames)
    {
        var byName = columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var missingColumns = new List<PromotedField>();
        var missingIndexes = new List<PromotedField>();

        foreach (var field in layout.Promoted)
        {
            if (byName.TryGetValue(field.Column, out var existing))
            {
                if (!field.Type.Matches(existing.TypeName, existing.MaxLength, existing.Scale))
                    throw new InvalidOperationException(
                        $"Promoted field '{field.Key}' expects column [{field.Column}] {field.Type.SqlTypeName}, but the table has {Describe(existing)}. " +
                        "Vestige never alters existing columns; rename the column or change the promoted type.");
                if (!existing.IsNullable)
                    throw new InvalidOperationException(
                        $"Promoted column [{field.Column}] exists but is NOT NULL. Promoted columns must be nullable because events without '{field.Key}' write NULL.");
            }
            else
            {
                missingColumns.Add(field);
            }

            if (field.Indexed && !indexNames.Contains(layout.IndexName(field)))
                missingIndexes.Add(field);
        }

        return new SchemaDiff(missingColumns, missingIndexes);
    }

    public void RequireNoMissingColumns()
    {
        if (MissingColumns.Count == 0)
            return;

        throw new InvalidOperationException(
            $"AutoCreate is disabled and promoted column(s) {string.Join(", ", MissingColumns.Select(f => $"[{f.Column}]"))} are missing. " +
            "Add them or enable AutoCreate.");
    }

    private static string Describe(ExistingColumn column) => column.TypeName switch
    {
        "nvarchar" => column.MaxLength == -1 ? "nvarchar(max)" : $"nvarchar({column.MaxLength / 2})",
        "varchar" => column.MaxLength == -1 ? "varchar(max)" : $"varchar({column.MaxLength})",
        "datetimeoffset" => $"datetimeoffset({column.Scale})",
        _ => column.TypeName,
    };
}
