using Vestige.Retention;

namespace Vestige.Tests;

public sealed class RetentionPlannerTests
{
    [Fact]
    public void Plan_Disabled_ReturnsNoPasses()
    {
        Assert.Empty(RetentionPlanner.Plan(new RetentionOptions()));
    }

    [Fact]
    public void Plan_DefaultOnly_SinglePassWithoutExclusions()
    {
        var passes = RetentionPlanner.Plan(new RetentionOptions { Default = TimeSpan.FromDays(30) });

        var pass = Assert.Single(passes);
        Assert.Equal(TimeSpan.FromDays(30), pass.Period);
        Assert.Empty(pass.KeepWhenPresent);
    }

    [Fact]
    public void Plan_TwoTiers_ShortPassSparesLongerRule()
    {
        var options = new RetentionOptions { Default = TimeSpan.FromDays(30) };
        options.KeepLonger("audit_action", TimeSpan.FromDays(365));

        var passes = RetentionPlanner.Plan(options);

        Assert.Collection(
            passes,
            p =>
            {
                Assert.Equal(TimeSpan.FromDays(30), p.Period);
                Assert.Equal(new[] { "audit_action" }, p.KeepWhenPresent);
            },
            p =>
            {
                Assert.Equal(TimeSpan.FromDays(365), p.Period);
                Assert.Empty(p.KeepWhenPresent);
            });
    }

    [Fact]
    public void Plan_SharedAndMiddlePeriods_OrdersAscendingAndExcludesOnlyLongerRules()
    {
        var options = new RetentionOptions { Default = TimeSpan.FromDays(30) };
        options.KeepLonger("audit_action", TimeSpan.FromDays(365));
        options.KeepLonger("billing_id", TimeSpan.FromDays(90));
        options.KeepLonger("security_event", TimeSpan.FromDays(365));

        var passes = RetentionPlanner.Plan(options);

        Assert.Equal(
            new[] { TimeSpan.FromDays(30), TimeSpan.FromDays(90), TimeSpan.FromDays(365) },
            passes.Select(p => p.Period));
        Assert.Equal(new[] { "audit_action", "billing_id", "security_event" }, passes[0].KeepWhenPresent);
        Assert.Equal(new[] { "audit_action", "security_event" }, passes[1].KeepWhenPresent);
        Assert.Empty(passes[2].KeepWhenPresent);
    }

    [Fact]
    public void Plan_InvalidOptions_Throws()
    {
        var options = new RetentionOptions();
        options.KeepLonger("audit_action", TimeSpan.FromDays(365));

        Assert.Throws<InvalidOperationException>(() => RetentionPlanner.Plan(options));
    }
}
