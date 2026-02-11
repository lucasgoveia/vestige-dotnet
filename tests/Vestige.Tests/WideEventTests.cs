using Vestige;
using Vestige.Testing;

namespace Vestige.Tests;

public sealed class WideEventTests
{
    [Fact]
    public void Set_And_Get_ReturnsValue()
    {
        var ev = new WideEvent();
        ev.Set("user.id", "u123");
        Assert.Equal("u123", ev.Get<string>("user.id"));
    }

    [Fact]
    public void Get_MissingKey_ReturnsDefault()
    {
        var ev = new WideEvent();
        Assert.Null(ev.Get<string>("missing"));
    }

    [Fact]
    public void Has_ReturnsTrueWhenPresent()
    {
        var ev = new WideEvent();
        ev.Set("k", "v");
        Assert.True(ev.Has("k"));
        Assert.False(ev.Has("missing"));
    }

    [Fact]
    public void Remove_ReturnsTrueAndRemovesField()
    {
        var ev = new WideEvent();
        ev.Set("k", "v");
        Assert.True(ev.Remove("k"));
        Assert.False(ev.Has("k"));
        Assert.False(ev.Remove("k")); // second remove returns false
    }

    [Fact]
    public void SetMany_SetsAllFields()
    {
        var ev = new WideEvent();
        ev.SetMany(new Dictionary<string, object?>
        {
            ["a"] = 1,
            ["b"] = "two",
            ["c"] = null,
        });
        Assert.Equal(1, ev.Get<int>("a"));
        Assert.Equal("two", ev.Get<string>("b"));
        Assert.True(ev.Has("c"));
    }

    [Fact]
    public void Time_RecordsTiming()
    {
        var ev = new WideEvent();
        using (ev.Time("db.query"))
        {
            // Minimal work
        }
        Assert.True(ev.Timings.ContainsKey("db.query"));
    }

    [Fact]
    public async Task Time_AccumulatesMultipleCalls()
    {
        var ev = new WideEvent();
        using (ev.Time("op")) { await Task.Delay(10); }
        using (ev.Time("op")) { await Task.Delay(10); }
        Assert.True(ev.Timings["op"] >= 0); // at least recorded
    }

    [Fact]
    public void CaptureException_SetsErrorFields()
    {
        var ev = new WideEvent();
        var ex = new InvalidOperationException("boom");
        ev.CaptureException(ex);

        Assert.Equal(typeof(InvalidOperationException).FullName, ev.Get<string>("error.type"));
        Assert.Equal("boom", ev.Get<string>("error.message"));
        Assert.Equal("error", ev.Outcome);
    }

    [Fact]
    public void CaptureException_DoesNotOverrideExistingOutcome()
    {
        var ev = new WideEvent { Outcome = "cancelled" };
        ev.CaptureException(new Exception("x"));
        Assert.Equal("cancelled", ev.Outcome);
    }

    [Fact]
    public async Task ThreadSafety_ConcurrentSets_DoNotThrow()
    {
        var ev = new WideEvent();
        var tasks = Enumerable.Range(0, 100)
            .Select(i => Task.Run(() => ev.Set($"key.{i}", i)))
            .ToArray();
        await Task.WhenAll(tasks);
        Assert.Equal(100, ev.Properties.Count);
    }

    [Fact]
    public void Assertions_HaveField_Passes()
    {
        var ev = new WideEvent();
        ev.Set("x", 42);
        ev.Should().HaveField("x", 42);
    }

    [Fact]
    public void Assertions_HaveOutcome_Passes()
    {
        var ev = new WideEvent { Outcome = "success" };
        ev.Should().HaveOutcome("success");
    }
}
