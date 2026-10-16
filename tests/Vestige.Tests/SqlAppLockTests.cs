using Microsoft.Data.SqlClient;
using Vestige.Sinks.SqlServer;

namespace Vestige.Tests;

public sealed class SqlAppLockTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(-1, false)]
    [InlineData(-3, false)]
    public void Interpret_GrantedOrBusy(int result, bool expected)
    {
        Assert.Equal(expected, SqlAppLock.Interpret(result, "vestige|[dbo].[wide_events]|retention"));
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-999)]
    public void Interpret_CallFailure_Throws(int result)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SqlAppLock.Interpret(result, "vestige|x|retention"));
        Assert.Contains(result.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message);
    }

    [Fact]
    public void LockConnectionString_DisablesPoolingAndKeepsEverythingElse()
    {
        var cs = SqlAppLock.LockConnectionString("Server=db;Database=ops;User ID=u;Password=p;TrustServerCertificate=True");

        var builder = new SqlConnectionStringBuilder(cs);
        Assert.False(builder.Pooling);
        Assert.Equal("db", builder.DataSource);
        Assert.Equal("ops", builder.InitialCatalog);
        Assert.True(builder.TrustServerCertificate);
    }

    [Fact]
    public void Resource_IsInjectiveForTablesWhoseUnderscoredPrefixesCollide()
    {
        Assert.NotEqual(
            SqlAppLock.Resource("[dbo].[wide.events]", "retention"),
            SqlAppLock.Resource("[dbo].[wide_events]", "retention"));
        Assert.NotEqual(
            SqlAppLock.Resource("[a_b].[c]", "retention"),
            SqlAppLock.Resource("[a].[b_c]", "retention"));
    }

    [Fact]
    public void Resource_LongNames_HashedWithin255AndStillDistinct()
    {
        var table = "[dbo].[" + new string('t', 120) + "]";

        var a = SqlAppLock.Resource(table, "index|" + new string('c', 128));
        var b = SqlAppLock.Resource(table, "index|" + new string('c', 127) + "d");

        Assert.True(a.Length <= 255, $"length {a.Length}");
        Assert.True(b.Length <= 255, $"length {b.Length}");
        Assert.NotEqual(a, b);
    }
}
