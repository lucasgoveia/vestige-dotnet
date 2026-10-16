using Vestige.Sinks.SqlServer;

namespace Vestige.Tests;

public sealed class PromotedFieldTests
{
    private static SqlServerSinkOptions Options() => new() { ConnectionString = "Server=.;Database=x" };

    [Theory]
    [InlineData("audit_action", "audit_action")]
    [InlineData("user.id", "user_id")]
    [InlineData("Tenant-ID", "tenant_id")]
    public void Add_DerivesColumnFromKey(string key, string expectedColumn)
    {
        var fields = new PromotedFieldCollection();

        fields.Add(key, PromotedType.String(64));

        var field = Assert.Single(fields);
        Assert.Equal(key, field.Key);
        Assert.Equal(expectedColumn, field.Column);
        Assert.True(field.Indexed);
    }

    [Fact]
    public void Add_HonoursExplicitColumnAndIndexedFlag()
    {
        var fields = new PromotedFieldCollection();

        fields.Add("user.id", PromotedType.String(64), column: "uid", indexed: false);

        var field = Assert.Single(fields);
        Assert.Equal("uid", field.Column);
        Assert.False(field.Indexed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4001)]
    public void String_LengthOutOfRange_Throws(int length)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PromotedType.String(length));
    }

    [Fact]
    public void NewOptions_HaveNoPromotedFieldsAndRetentionOff()
    {
        var options = Options();

        Assert.Empty(options.PromotedFields);
        Assert.False(options.Retention.Enabled);
        SqlServerSinkOptionsValidator.Validate(options);
    }

    [Fact]
    public void Validate_DefaultColumnCollidingWithFixedColumn_Throws()
    {
        var options = Options();
        options.PromotedFields.Add("http.method", PromotedType.String(16));

        var ex = Assert.Throws<InvalidOperationException>(() => SqlServerSinkOptionsValidator.Validate(options));
        Assert.Contains("[http_method]", ex.Message);
        Assert.Contains("reserved", ex.Message);
    }

    [Fact]
    public void Validate_ColumnNamedId_Throws()
    {
        var options = Options();
        options.PromotedFields.Add("ID", PromotedType.Long);

        var ex = Assert.Throws<InvalidOperationException>(() => SqlServerSinkOptionsValidator.Validate(options));
        Assert.Contains("[id]", ex.Message);
    }

    [Fact]
    public void Validate_DuplicateKey_Throws()
    {
        var options = Options();
        options.PromotedFields.Add("tenant_id", PromotedType.Guid);
        options.PromotedFields.Add("tenant_id", PromotedType.Guid, column: "tenant_id_2");

        var ex = Assert.Throws<InvalidOperationException>(() => SqlServerSinkOptionsValidator.Validate(options));
        Assert.Contains("'tenant_id' is promoted more than once", ex.Message);
    }

    [Fact]
    public void Validate_DuplicateColumnIgnoringCase_Throws()
    {
        var options = Options();
        options.PromotedFields.Add("a.b", PromotedType.Int);
        options.PromotedFields.Add("x", PromotedType.Int, column: "A_B");

        var ex = Assert.Throws<InvalidOperationException>(() => SqlServerSinkOptionsValidator.Validate(options));
        Assert.Contains("used by more than one promoted field", ex.Message);
    }

    [Theory]
    [InlineData("1abc")]
    [InlineData("bad name")]
    [InlineData("semi;colon")]
    public void Validate_InvalidColumnName_Throws(string column)
    {
        var options = Options();
        options.PromotedFields.Add("k", PromotedType.Int, column: column);

        var ex = Assert.Throws<InvalidOperationException>(() => SqlServerSinkOptionsValidator.Validate(options));
        Assert.Contains("must start with a letter or underscore", ex.Message);
    }

    [Fact]
    public void Validate_ColumnLongerThan128_Throws()
    {
        var options = Options();
        options.PromotedFields.Add("k", PromotedType.Int, column: new string('c', 129));

        var ex = Assert.Throws<InvalidOperationException>(() => SqlServerSinkOptionsValidator.Validate(options));
        Assert.Contains("128", ex.Message);
    }

    [Fact]
    public void Validate_RetentionRuleOnUnpromotedKey_Throws()
    {
        var options = Options();
        options.Retention.Default = TimeSpan.FromDays(30);
        options.Retention.KeepLonger("audit_action", TimeSpan.FromDays(365));

        var ex = Assert.Throws<InvalidOperationException>(() => SqlServerSinkOptionsValidator.Validate(options));
        Assert.Equal("retention rule on 'audit_action' requires a promoted field", ex.Message);
    }

    [Fact]
    public void Validate_RetentionRuleOnPromotedKey_Passes()
    {
        var options = Options();
        options.PromotedFields.Add("audit_action", PromotedType.String(128), indexed: false);
        options.Retention.Default = TimeSpan.FromDays(30);
        options.Retention.KeepLonger("audit_action", TimeSpan.FromDays(365));

        SqlServerSinkOptionsValidator.Validate(options);
    }

    [Fact]
    public void Validate_InvalidRetention_Throws()
    {
        var options = Options();
        options.Retention.Default = TimeSpan.Zero;

        Assert.Throws<InvalidOperationException>(() => SqlServerSinkOptionsValidator.Validate(options));
    }

    [Theory]
    [InlineData("nvarchar", 256, 0, true)]
    [InlineData("nvarchar", 512, 0, false)]
    [InlineData("varchar", 128, 0, false)]
    public void StringType_MatchesSysColumnsByteLength(string typeName, short maxLength, byte scale, bool expected)
    {
        Assert.Equal(expected, PromotedType.String(128).Matches(typeName, maxLength, scale));
    }

    [Fact]
    public void DateTimeOffsetType_RequiresScale7()
    {
        Assert.True(PromotedType.DateTimeOffset.Matches("datetimeoffset", 10, 7));
        Assert.False(PromotedType.DateTimeOffset.Matches("datetimeoffset", 8, 3));
    }
}
