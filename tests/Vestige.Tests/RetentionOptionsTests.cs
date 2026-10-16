using Vestige.Retention;

namespace Vestige.Tests;

public sealed class RetentionOptionsTests
{
    [Fact]
    public void NewOptions_AreDisabled_AndValidate()
    {
        var options = new RetentionOptions();

        options.Validate();

        Assert.False(options.Enabled);
        Assert.Empty(options.Rules);
    }

    [Fact]
    public void KeepLonger_WithoutDefault_Throws()
    {
        var options = new RetentionOptions();
        options.KeepLonger("audit_action", TimeSpan.FromDays(365));

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("requires Retention.Default", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Default_NotPositive_Throws(int days)
    {
        var options = new RetentionOptions { Default = TimeSpan.FromDays(days) };

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Retention.Default must be positive", ex.Message);
    }

    [Fact]
    public void Rule_NotLongerThanDefault_Throws()
    {
        var options = new RetentionOptions { Default = TimeSpan.FromDays(30) };
        options.KeepLonger("audit_action", TimeSpan.FromDays(30));

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("'audit_action'", ex.Message);
        Assert.Contains("must keep longer than Retention.Default", ex.Message);
    }

    [Fact]
    public void Rule_DeclaredTwice_Throws()
    {
        var options = new RetentionOptions { Default = TimeSpan.FromDays(30) };
        options.KeepLonger("audit_action", TimeSpan.FromDays(90));
        options.KeepLonger("audit_action", TimeSpan.FromDays(365));

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("declared more than once", ex.Message);
    }

    [Fact]
    public void Period_BeyondIntSeconds_Throws()
    {
        var options = new RetentionOptions { Default = TimeSpan.FromSeconds((double)int.MaxValue + 1) };

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("cannot exceed", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void KeepLonger_BlankKey_Throws(string key)
    {
        var options = new RetentionOptions();

        Assert.ThrowsAny<ArgumentException>(() => options.KeepLonger(key, TimeSpan.FromDays(1)));
    }

    [Fact]
    public void Enabled_WhenDefaultSet()
    {
        var options = new RetentionOptions { Default = TimeSpan.FromDays(30) };

        Assert.True(options.Enabled);
    }
}
