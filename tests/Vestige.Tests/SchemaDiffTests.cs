using Vestige.Sinks.SqlServer;

namespace Vestige.Tests;

public sealed class SchemaDiffTests
{
    private static SqlServerTableLayout Layout()
    {
        var options = new SqlServerSinkOptions { ConnectionString = "Server=.;Database=x" };
        options.PromotedFields.Add("audit_action", PromotedType.String(128));
        options.PromotedFields.Add("tenant_id", PromotedType.Guid);
        options.PromotedFields.Add("user.id", PromotedType.String(64), indexed: false);
        return new SqlServerTableLayout(options);
    }

    private static readonly ExistingColumn AuditAction = new("audit_action", "nvarchar", 256, 0, true);
    private static readonly ExistingColumn TenantId = new("tenant_id", "uniqueidentifier", 16, 0, true);
    private static readonly ExistingColumn UserId = new("user_id", "nvarchar", 128, 0, true);

    private static HashSet<string> Indexes(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Compute_FreshUpgrade_AllColumnsAndIndexedFieldsMissing()
    {
        var diff = SchemaDiff.Compute(Layout(), [], Indexes());

        Assert.Equal(new[] { "audit_action", "tenant_id", "user_id" }, diff.MissingColumns.Select(f => f.Column));
        Assert.Equal(new[] { "audit_action", "tenant_id" }, diff.MissingIndexes.Select(f => f.Column));
    }

    [Fact]
    public void Compute_UpToDate_NothingMissing()
    {
        var diff = SchemaDiff.Compute(
            Layout(),
            [AuditAction, TenantId, UserId],
            Indexes("DBO_WIDE_EVENTS_AUDIT_ACTION_IDX", "dbo_wide_events_tenant_id_idx"));

        Assert.Empty(diff.MissingColumns);
        Assert.Empty(diff.MissingIndexes);
    }

    [Fact]
    public void Compute_ColumnPresentIndexMissing_OnlyIndexPending()
    {
        var diff = SchemaDiff.Compute(Layout(), [AuditAction, TenantId, UserId], Indexes("dbo_wide_events_audit_action_idx"));

        Assert.Empty(diff.MissingColumns);
        Assert.Equal("tenant_id", Assert.Single(diff.MissingIndexes).Column);
    }

    [Fact]
    public void Compute_TypeMismatch_Throws()
    {
        var wrong = TenantId with { TypeName = "int", MaxLength = 4 };

        var ex = Assert.Throws<InvalidOperationException>(
            () => SchemaDiff.Compute(Layout(), [AuditAction, wrong, UserId], Indexes()));
        Assert.Contains("'tenant_id'", ex.Message);
        Assert.Contains("UNIQUEIDENTIFIER", ex.Message);
        Assert.Contains("int", ex.Message);
    }

    [Fact]
    public void Compute_StringLengthMismatch_Throws()
    {
        var wider = AuditAction with { MaxLength = 512 };

        var ex = Assert.Throws<InvalidOperationException>(
            () => SchemaDiff.Compute(Layout(), [wider, TenantId, UserId], Indexes()));
        Assert.Contains("nvarchar(256)", ex.Message);
    }

    [Fact]
    public void Compute_ExistingNotNullColumn_Throws()
    {
        var notNull = TenantId with { IsNullable = false };

        var ex = Assert.Throws<InvalidOperationException>(
            () => SchemaDiff.Compute(Layout(), [AuditAction, notNull, UserId], Indexes()));
        Assert.Contains("NOT NULL", ex.Message);
    }

    [Fact]
    public void RequireNoMissingColumns_ListsMissingColumns()
    {
        var diff = SchemaDiff.Compute(Layout(), [AuditAction], Indexes());

        var ex = Assert.Throws<InvalidOperationException>(diff.RequireNoMissingColumns);
        Assert.Contains("[tenant_id], [user_id]", ex.Message);
        Assert.Contains("AutoCreate is disabled", ex.Message);
    }
}
