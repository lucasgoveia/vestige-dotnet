using System.Text;
using Vestige;
using Vestige.Internal;

namespace Vestige.Tests;

public sealed class WideEventLimitsTests
{
    [Fact]
    public void LongStringValue_IsTruncated()
    {
        var ev = new WideEvent(new WideEventLimits { MaxValueLength = 16 });
        ev.Set("k", new string('x', 1000));

        var value = ev.Get<string>("k")!;
        Assert.Equal(16 + WideEventLimits.TruncationSuffix.Length, value.Length);
        Assert.EndsWith(WideEventLimits.TruncationSuffix, value);
    }

    [Fact]
    public void LongKey_IsTruncated()
    {
        var ev = new WideEvent(new WideEventLimits { MaxKeyLength = 8 });
        ev.Set(new string('k', 100), 1);

        var key = Assert.Single(ev.Properties).Key;
        Assert.Equal(8 + WideEventLimits.TruncationSuffix.Length, key.Length);
    }

    [Fact]
    public void NonStringValues_AreNotTruncated()
    {
        var ev = new WideEvent(new WideEventLimits { MaxValueLength = 2 });
        ev.Set("n", 1234567);
        Assert.Equal(1234567, ev.Get<int>("n"));
    }

    [Fact]
    public void FieldsBeyondMaxCount_AreDroppedAndCounted()
    {
        var ev = new WideEvent(new WideEventLimits { MaxFieldCount = 3 });

        for (int i = 0; i < 10; i++)
            ev.Set($"k{i}", i);

        Assert.Equal(3, ev.Properties.Count);
        Assert.Equal(7, ev.DroppedFieldCount);
    }

    [Fact]
    public void OverwritingAnExistingKey_IsAllowedAtCapacity()
    {
        var ev = new WideEvent(new WideEventLimits { MaxFieldCount = 2 });
        ev.Set("a", 1);
        ev.Set("b", 2);
        ev.Set("a", 99); // overwrite must not count as a new field

        Assert.Equal(99, ev.Get<int>("a"));
        Assert.Equal(0, ev.DroppedFieldCount);
    }

    [Fact]
    public void DroppedFieldCount_IsSurfacedOnTheSerializedEvent()
    {
        var ev = new WideEvent(new WideEventLimits { MaxFieldCount = 1 });
        ev.Set("a", 1);
        ev.Set("b", 2);

        var data = new SystemTextJsonSerializer().Serialize(ev);
        Assert.Equal(1, data.Fields["vestige.dropped_fields"]);
    }

    [Fact]
    public void NoDroppedFields_OmitsTheCounterField()
    {
        var ev = new WideEvent();
        ev.Set("a", 1);

        var data = new SystemTextJsonSerializer().Serialize(ev);
        Assert.False(data.Fields.ContainsKey("vestige.dropped_fields"));
    }

    [Fact]
    public void CaptureException_TruncatesStackTrace()
    {
        var ev = new WideEvent(new WideEventLimits { MaxStackTraceLength = 32 });

        Exception captured;
        try { throw new InvalidOperationException("boom"); }
        catch (Exception ex) { captured = ex; }

        ev.CaptureException(captured);

        var stack = ev.Get<string>("error.stack_trace")!;
        Assert.True(stack.Length <= 32 + WideEventLimits.TruncationSuffix.Length);
        Assert.Equal("boom", ev.Get<string>("error.message"));
    }

    [Fact]
    public void Truncation_DoesNotSplitSurrogatePairs()
    {
        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 20));

        // An odd cut lands mid-pair; the orphaned half would serialize as U+FFFD.
        var ev = new WideEvent(new WideEventLimits { MaxValueLength = 5 });
        ev.Set("k", emoji);

        var value = ev.Get<string>("k")!;
        var body = value[..^WideEventLimits.TruncationSuffix.Length];

        Assert.False(char.IsHighSurrogate(body[^1]), "truncation left an orphaned high surrogate");

        // A lone surrogate does not survive a UTF-8 round trip; it becomes U+FFFD.
        Assert.Equal(body, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public void TruncatedSurrogateValue_SerializesWithoutCorruption()
    {
        var ev = new WideEvent(new WideEventLimits { MaxValueLength = 5 });
        ev.Set("k", string.Concat(Enumerable.Repeat("\U0001F600", 20)));

        var json = Encoding.UTF8.GetString(new SystemTextJsonSerializer().Serialize(ev).JsonBytes);

        Assert.DoesNotContain("\uFFFD", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidLimits_AreRejected(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WideEventLimits { MaxFieldCount = value }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WideEventLimits { MaxKeyLength = value }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WideEventLimits { MaxValueLength = value }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WideEventLimits { MaxStackTraceLength = value }.Validate());
    }
}
