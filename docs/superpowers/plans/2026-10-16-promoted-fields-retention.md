# Promoted Fields & Retention Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship Vestige 0.3.0. The SQL Server sink gets typed, indexed *promoted fields*, and both the SQL Server and Postgres sinks get opt-in *tiered retention*.

**Architecture:**
- **Retention is shared.** Core `Vestige` gets a sink-agnostic retention model (`RetentionOptions` → `RetentionPlanner` → ordered `RetentionPass`es) and a `RetentionLoop` that drives any `IRetentionStore`. Each sink only implements its store: the lock, plus one batched delete.
- **The SQL Server sink gets a `SqlServerTableLayout`**, built once at init. It owns column lists, INSERT text and the bulk-copy schema.
- **Schema DDL is built by pure functions** (`SqlServerSchemaSql`, `SchemaDiff`).
- **Index builds on existing tables run in the background** (`SqlServerIndexBuilder`), guarded by `sp_getapplock`, after `InitializeAsync` has already returned.

**Tech Stack:**
- Runtime: .NET 8/9/10 (multi-target), Microsoft.Data.SqlClient 5.2.2, Npgsql 10.0.1, Microsoft.Extensions.Logging.Abstractions, `TimeProvider`.
- Tests: xUnit v3 (MTP), Testcontainers 4.4.0, Microsoft.Extensions.TimeProvider.Testing 9.0.0.

**Spec:** `docs/superpowers/specs/2026-10-16-promoted-fields-retention-design.md`

## Global Constraints

- **Target frameworks** come from `Directory.Build.props`: `net8.0;net9.0;net10.0`. `TreatWarningsAsErrors` is true for `src/`, so no warnings are allowed there. Test projects target `net9.0`.
- **Additive only.** With no `PromotedFields` and no `Retention.Default`, the sink must produce the same table schema, the same INSERT column list and the same behaviour as 0.2.0.
- **Retention is off by default.** `Retention.Default == null` means no loop is started.
- **Hardcoded constants, not options:**
  - Retention batch size `4000`, pause between batches `100 ms`, first cycle `1 min` after init, interval `1 h`.
  - Index build `CommandTimeout = 0`.
  - Retention delete `CommandTimeout = 300`.
  - Promotion-warning window `1 min`.
- **Never log field values.** Log field keys, column names and CLR type names only.
- **Promoted columns are always `NULL`able.** Vestige never alters or drops a column.
- **Index on a promoted field:** `([col], [timestamp_utc] DESC)`, named `{prefix}_{column}_idx`, where `prefix = $"{Schema}_{Table}".Replace(".", "_")`.
- **Online index build** only when `SERVERPROPERTY('EngineEdition')` is in `{3, 5, 8}`.
- **Commits:** Conventional Commits, GPG-signed (repo default). **No `Co-Authored-By` trailer.**
- **Test commands** (xUnit v3 / MTP):
  - Unit tests: `dotnet test tests/Vestige.Tests -- --filter-class "Vestige.Tests.<Class>"`
  - Integration tests: `dotnet test tests/Vestige.Sinks.IntegrationTests -- --filter-class "Vestige.Sinks.IntegrationTests.<Class>"`
  - Full suite: `dotnet test Vestige.slnx`

### Deliberate deviations from the spec (decided while planning)

1. **The spec says `INCLUDE ([id])`, but the index has no INCLUDE.** `id` is the clustered PK, so every nonclustered index already carries it, and for a non-unique index it is part of the key too. Including it again is redundant.
2. **A delete pass stops when a batch returns fewer than 4000 rows**, not only when a batch returns 0. The outcome is the same, with one round trip saved.
3. **Spec §4.3 and §6.6 conflict.** §4.3 says that with `AutoCreate = false`, a missing promoted column makes `InitializeAsync` throw, and every SQL Server retention rule key must be a promoted field. So the §6.6 branch, "a missing rule column disables retention", can never be reached on SQL Server. Postgres rules don't need columns. No code is written for §6.6's branch.
4. **Retention periods are capped at `int.MaxValue` seconds (~68 years)**, because T-SQL `DATEADD` takes an `int`. This is validated in `RetentionOptions.Validate()`.

## Review Focus

1. **A schema or table name containing `.`, `]` or `'`** (e.g. `Table = "wide.events"`). Expected: DDL is quoted correctly, and index and lock names are sanitized. Pinned in Task 5 (layout names) and Task 6 (`CreateIndex` / `CreateTable` quoting).
2. **A promoted key whose default column name collides with a fixed column** (`"http.method"` → `http_method`). Expected: a clear startup error that names the column, not a SQL error at the first write. Pinned in Task 3.
3. **A field explicitly set to null** (`ev.Set("tenant_id", null)`). Expected: NULL in the column, with no warning logged. Pinned in Task 4.
4. **An operator pre-created the promoted column as `NOT NULL`.** Expected: `InitializeAsync` fails loudly, instead of every batch later failing on NULL inserts. Pinned in Task 6 (`SchemaDiff`) and Task 7 (integration).
5. **Host shutdown during the retention initial delay or in the middle of a cycle.** Expected: the loop exits quietly, with no Error logged and no exception surfacing from `DisposeAsync`. Pinned in Task 2.

---

## File Structure

**Core `src/Vestige/Retention/`** (new, public API in namespace `Vestige.Retention`)

| File | Responsibility |
|---|---|
| `RetentionOptions.cs` | `Default`, `KeepLonger`, `Rules`, `Enabled`, `Validate()` |
| `RetentionRule.cs` | `record RetentionRule(string Key, TimeSpan Period)` |
| `RetentionPass.cs` | `record RetentionPass(TimeSpan Period, IReadOnlyList<string> KeepWhenPresent)` |
| `RetentionPlanner.cs` | `static IReadOnlyList<RetentionPass> Plan(RetentionOptions)` |
| `IRetentionStore.cs` | Sink-side lock + batched delete contract |
| `RetentionLoop.cs` | Schedule, passes, batching, logging; `RunAsync`, `RunCycleAsync` |
| `RetentionCycleResult.cs` | Outcome of one cycle |

**SQL Server sink `src/Vestige.Sinks.SqlServer/`**

| File | Responsibility |
|---|---|
| `PromotedType.cs` (new) | Public type factory; SQL type name, `SqlDbType`, CLR type, `Matches(sys.columns)` |
| `PromotedField.cs` (new) | `record PromotedField(Key, Type, Column, Indexed)` |
| `PromotedFieldCollection.cs` (new) | `Add(...)`, default column naming |
| `SqlServerSinkOptionsValidator.cs` (new) | Column / key / retention-rule validation |
| `PromotedValueConverter.cs` (new) | `TryConvert(PromotedType, object, out object?)` |
| `PromotionWarningLimiter.cs` (new) | Once-per-field-per-minute conversion warning |
| `SqlServerTableLayout.cs` (new) | Names, quoting, INSERT text, bulk-copy schema, promoted params |
| `SqlServerSchemaSql.cs` (new) | Pure DDL/query text builders |
| `SchemaDiff.cs` (new) | `ExistingColumn`, diff, mismatch errors |
| `IndexBuildStrategy.cs` (new) | Edition → `Online` / `Skip` |
| `SqlAppLock.cs` (new) | `sp_getapplock` / `sp_releaseapplock` helpers |
| `SqlServerIndexBuilder.cs` (new) | Background online index builds |
| `SqlServerRetentionSql.cs` (new) | Pure DELETE text builder |
| `SqlServerRetentionStore.cs` (new) | `IRetentionStore` for SQL Server |
| `Properties/AssemblyInfo.cs` (new) | `InternalsVisibleTo` for both test projects |
| `SqlServerSinkOptions.cs` (modify) | `PromotedFields`, `Retention` |
| `SqlServerEventRow.cs` (modify) | `PromotedValues`, `Map(data, promoted, warnings)`, `Truncate` internal |
| `ISqlServerWriter.cs` / `SqlServerWriter.cs` (modify) | Layout-driven writes; `EnsureStorageAsync` returns pending indexes; `VerifyStorageAsync` |
| `SqlServerSink.cs` (modify) | Logger/TimeProvider, layout, background index build, retention loop, disposal |

**Postgres sink `src/Vestige.Sinks.Postgres/`**

| File | Responsibility |
|---|---|
| `PostgresRetentionSql.cs` (new) | Pure DELETE text builder |
| `PostgresRetentionStore.cs` (new) | `IRetentionStore` for Postgres |
| `PostgresSinkOptions.cs`, `PostgresSink.cs`, `Properties/AssemblyInfo.cs` (modify) | Retention wiring |

**Tests**

- `tests/Vestige.Tests/` (existing project):
  - New test classes per task.
  - `TestSupport/ListLogger.cs` and `TestSupport/FakeSqlServerWriter.cs`.
- `tests/Vestige.Sinks.IntegrationTests/` (new project):
  - Testcontainers fixtures, `Legacy020Schema`, `ListLogger`, `TestEvents`.
  - SQL Server and Postgres test classes.

---

### Task 1: Core retention model and planner

**Files:**
- Create: `src/Vestige/Retention/RetentionRule.cs`, `src/Vestige/Retention/RetentionPass.cs`, `src/Vestige/Retention/RetentionOptions.cs`, `src/Vestige/Retention/RetentionPlanner.cs`
- Test: `tests/Vestige.Tests/RetentionOptionsTests.cs`, `tests/Vestige.Tests/RetentionPlannerTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `Vestige.Retention.RetentionRule(string Key, TimeSpan Period)`
  - `RetentionPass(TimeSpan Period, IReadOnlyList<string> KeepWhenPresent)`
  - `RetentionOptions { TimeSpan? Default; IReadOnlyList<RetentionRule> Rules; bool Enabled; RetentionOptions KeepLonger(string, TimeSpan); void Validate(); }`
  - `static RetentionPlanner.Plan(RetentionOptions) → IReadOnlyList<RetentionPass>` (validates first)

- [ ] **Step 1: Write the failing tests**

`tests/Vestige.Tests/RetentionOptionsTests.cs`:

```csharp
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
```

`tests/Vestige.Tests/RetentionPlannerTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Vestige.Tests -- --filter-class "Vestige.Tests.RetentionOptionsTests" --filter-class "Vestige.Tests.RetentionPlannerTests"`
Expected: the build FAILS with `CS0246: The type or namespace name 'RetentionOptions' could not be found`.

- [ ] **Step 3: Implement**

`src/Vestige/Retention/RetentionRule.cs`:

```csharp
namespace Vestige.Retention;

/// <summary>Keep events that carry <paramref name="Key"/> for <paramref name="Period"/>.</summary>
public sealed record RetentionRule(string Key, TimeSpan Period);
```

`src/Vestige/Retention/RetentionPass.cs`:

```csharp
namespace Vestige.Retention;

/// <summary>
/// One purge pass: delete events older than <paramref name="Period"/> that carry none of the
/// fields in <paramref name="KeepWhenPresent"/> (those belong to a longer-lived rule).
/// </summary>
public sealed record RetentionPass(TimeSpan Period, IReadOnlyList<string> KeepWhenPresent);
```

`src/Vestige/Retention/RetentionOptions.cs`:

```csharp
namespace Vestige.Retention;

/// <summary>Tiered retention for persistent sinks. Disabled until <see cref="Default"/> is set.</summary>
public sealed class RetentionOptions
{
    private static readonly TimeSpan MaxPeriod = TimeSpan.FromSeconds(int.MaxValue);
    private readonly List<RetentionRule> _rules = [];

    /// <summary>How long events matching no rule are kept. <c>null</c> disables retention.</summary>
    public TimeSpan? Default { get; set; }

    /// <summary>Rules that keep events carrying a field for longer than <see cref="Default"/>.</summary>
    public IReadOnlyList<RetentionRule> Rules => _rules;

    /// <summary>True when a purge loop should run.</summary>
    public bool Enabled => Default is not null;

    /// <summary>Keep events that carry <paramref name="key"/> for <paramref name="period"/>.</summary>
    public RetentionOptions KeepLonger(string key, TimeSpan period)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _rules.Add(new RetentionRule(key, period));
        return this;
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> when the configuration is inconsistent.</summary>
    public void Validate()
    {
        if (Default is not { } defaultPeriod)
        {
            if (_rules.Count > 0)
                throw new InvalidOperationException("Retention.KeepLonger requires Retention.Default to be set.");
            return;
        }

        if (defaultPeriod <= TimeSpan.Zero)
            throw new InvalidOperationException("Retention.Default must be positive.");
        if (defaultPeriod > MaxPeriod)
            throw new InvalidOperationException($"Retention.Default cannot exceed {MaxPeriod}.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in _rules)
        {
            if (!seen.Add(rule.Key))
                throw new InvalidOperationException($"Retention rule on '{rule.Key}' is declared more than once.");
            if (rule.Period <= defaultPeriod)
                throw new InvalidOperationException(
                    $"Retention rule on '{rule.Key}' must keep longer than Retention.Default ({defaultPeriod}).");
            if (rule.Period > MaxPeriod)
                throw new InvalidOperationException($"Retention rule on '{rule.Key}' cannot exceed {MaxPeriod}.");
        }
    }
}
```

`src/Vestige/Retention/RetentionPlanner.cs`:

```csharp
namespace Vestige.Retention;

/// <summary>Turns retention options into ordered purge passes.</summary>
public static class RetentionPlanner
{
    /// <summary>
    /// One pass per distinct period, shortest first. A pass spares rows carrying any field whose
    /// rule keeps longer than the pass period; rows are therefore kept for the longest rule they match.
    /// </summary>
    public static IReadOnlyList<RetentionPass> Plan(RetentionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (options.Default is not { } defaultPeriod)
            return [];

        return options.Rules
            .Select(r => r.Period)
            .Append(defaultPeriod)
            .Distinct()
            .Order()
            .Select(period => new RetentionPass(
                period,
                options.Rules.Where(r => r.Period > period).Select(r => r.Key).ToArray()))
            .ToArray();
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Vestige.Tests -- --filter-class "Vestige.Tests.RetentionOptionsTests" --filter-class "Vestige.Tests.RetentionPlannerTests"`
Expected: PASS (15 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Vestige/Retention tests/Vestige.Tests/RetentionOptionsTests.cs tests/Vestige.Tests/RetentionPlannerTests.cs
git commit -m "feat(retention): add tiered retention options and pass planner"
```

---

### Task 2: Core retention loop

**Files:**
- Create: `src/Vestige/Retention/IRetentionStore.cs`, `src/Vestige/Retention/RetentionCycleResult.cs`, `src/Vestige/Retention/RetentionLoop.cs`, `tests/Vestige.Tests/TestSupport/ListLogger.cs`
- Modify: `Directory.Packages.props` (add `Microsoft.Extensions.TimeProvider.Testing`), `tests/Vestige.Tests/Vestige.Tests.csproj`
- Test: `tests/Vestige.Tests/RetentionLoopTests.cs`

**Interfaces:**
- Consumes: `RetentionPass` (Task 1).
- Produces:
  - `interface IRetentionStore { Task<IAsyncDisposable?> TryAcquireLockAsync(CancellationToken); Task<int> DeleteBatchAsync(RetentionPass pass, int batchSize, CancellationToken); }`
  - `record RetentionCycleResult(bool Acquired, IReadOnlyList<long> DeletedPerPass) { long TotalDeleted; static Skipped; }`
  - `sealed class RetentionLoop(IReadOnlyList<RetentionPass>, IRetentionStore, ILogger, TimeProvider)` with:
    - `const int BatchSize = 4000`
    - `static TimeSpan BatchPause, InitialDelay, Interval`
    - `Task RunAsync(CancellationToken)`
    - `Task<RetentionCycleResult> RunCycleAsync(CancellationToken)`
  - Test helper `Vestige.Tests.TestSupport.ListLogger<T>` with `IReadOnlyList<LogEntry> Entries` and `record LogEntry(LogLevel Level, string Message, Exception? Exception)`.

- [ ] **Step 1: Add the test dependency**

In `Directory.Packages.props`, inside `<ItemGroup Label="Test">`, add:

```xml
    <PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="9.0.0" />
```

In `tests/Vestige.Tests/Vestige.Tests.csproj`, inside the first `<ItemGroup>` with package references, add:

```xml
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
```

- [ ] **Step 2: Write the test logger**

`tests/Vestige.Tests/TestSupport/ListLogger.cs`:

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Vestige.Tests.TestSupport;

public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

public sealed class ListLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
}
```

- [ ] **Step 3: Write the failing tests**

`tests/Vestige.Tests/RetentionLoopTests.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Vestige.Retention;
using Vestige.Tests.TestSupport;

namespace Vestige.Tests;

public sealed class RetentionLoopTests
{
    private static readonly RetentionPass Short = new(TimeSpan.FromDays(30), ["audit_action"]);
    private static readonly RetentionPass Long = new(TimeSpan.FromDays(365), []);

    [Fact]
    public async Task RunCycle_LockHeldElsewhere_SkipsWithoutDeleting()
    {
        var store = new FakeStore { LockAvailable = false };
        var loop = new RetentionLoop([Short, Long], store, new ListLogger<RetentionLoop>(), TimeProvider.System);

        var result = await loop.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Acquired);
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task RunCycle_RunsPassesInOrder_UntilBatchIsShort()
    {
        var store = new FakeStore();
        store.Results[Short] = new Queue<int>([4000, 4000, 12]);
        store.Results[Long] = new Queue<int>([0]);
        var logger = new ListLogger<RetentionLoop>();
        var loop = new RetentionLoop([Short, Long], store, logger, TimeProvider.System);

        var result = await loop.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Acquired);
        Assert.Equal(new[] { 8012L, 0L }, result.DeletedPerPass);
        Assert.Equal(8012L, result.TotalDeleted);
        Assert.Equal(new[] { Short, Short, Short, Long }, store.Calls);
        Assert.All(store.BatchSizes, size => Assert.Equal(RetentionLoop.BatchSize, size));
        Assert.Equal(1, store.LocksReleased);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("8012"));
    }

    [Fact]
    public async Task RunCycle_NothingDeleted_LogsNoInformation()
    {
        var store = new FakeStore();
        var logger = new ListLogger<RetentionLoop>();
        var loop = new RetentionLoop([Short], store, logger, TimeProvider.System);

        await loop.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Information);
    }

    [Fact]
    public async Task RunCycle_DeleteThrows_ReleasesLockAndRethrows()
    {
        var store = new FakeStore { DeleteFailure = new InvalidOperationException("boom") };
        var loop = new RetentionLoop([Short], store, new ListLogger<RetentionLoop>(), TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => loop.RunCycleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, store.LocksReleased);
    }

    [Fact]
    public async Task RunAsync_WaitsInitialDelay_ThenRepeatsEveryInterval()
    {
        var time = new FakeTimeProvider();
        var store = new FakeStore { LockAvailable = false };
        var loop = new RetentionLoop([Short], store, new ListLogger<RetentionLoop>(), time);
        using var cts = new CancellationTokenSource();

        var run = loop.RunAsync(cts.Token);

        time.Advance(RetentionLoop.InitialDelay - TimeSpan.FromSeconds(1));
        Assert.False(await store.LockAttempts.WaitAsync(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken));

        Assert.True(await AdvanceUntilAsync(time, TimeSpan.FromSeconds(1), store.LockAttempts));
        Assert.True(await AdvanceUntilAsync(time, RetentionLoop.Interval, store.LockAttempts));

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task RunAsync_CycleFails_LogsErrorAndKeepsRunning()
    {
        var time = new FakeTimeProvider();
        var store = new FakeStore { LockFailure = new InvalidOperationException("db down") };
        var logger = new ListLogger<RetentionLoop>();
        var loop = new RetentionLoop([Short], store, logger, time);
        using var cts = new CancellationTokenSource();

        var run = loop.RunAsync(cts.Token);

        Assert.True(await AdvanceUntilAsync(time, RetentionLoop.InitialDelay, store.LockAttempts));
        Assert.True(await AdvanceUntilAsync(time, RetentionLoop.Interval, store.LockAttempts));
        cts.Cancel();
        await run;

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception?.Message == "db down");
    }

    [Fact]
    public async Task RunAsync_CancelledDuringInitialDelay_CompletesQuietly()
    {
        var logger = new ListLogger<RetentionLoop>();
        var loop = new RetentionLoop([Short], new FakeStore(), logger, new FakeTimeProvider());
        using var cts = new CancellationTokenSource();

        var run = loop.RunAsync(cts.Token);
        cts.Cancel();
        await run;

        Assert.True(run.IsCompletedSuccessfully);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task RunAsync_CancelledMidCycle_CompletesQuietly()
    {
        var time = new FakeTimeProvider();
        var store = new FakeStore { BlockDeletes = true };
        var logger = new ListLogger<RetentionLoop>();
        var loop = new RetentionLoop([Short], store, logger, time);
        using var cts = new CancellationTokenSource();

        var run = loop.RunAsync(cts.Token);
        Assert.True(await AdvanceUntilAsync(time, RetentionLoop.InitialDelay, store.LockAttempts));
        Assert.True(await store.DeleteStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        cts.Cancel();
        await run;

        Assert.True(run.IsCompletedSuccessfully);
        Assert.Equal(1, store.LocksReleased);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    // FakeTimeProvider only fires timers that already exist, and the loop registers its next
    // delay asynchronously, so keep advancing until the expected cycle is observed. Overshooting
    // can trigger extra cycles, which these tests tolerate.
    private static async Task<bool> AdvanceUntilAsync(FakeTimeProvider time, TimeSpan step, SemaphoreSlim signal)
    {
        for (var i = 0; i < 50; i++)
        {
            time.Advance(step);
            if (await signal.WaitAsync(TimeSpan.FromMilliseconds(100)))
                return true;
        }

        return false;
    }

    private sealed class FakeStore : IRetentionStore
    {
        public bool LockAvailable { get; init; } = true;
        public Exception? LockFailure { get; init; }
        public Exception? DeleteFailure { get; init; }
        public bool BlockDeletes { get; init; }
        public Dictionary<RetentionPass, Queue<int>> Results { get; } = [];
        public List<RetentionPass> Calls { get; } = [];
        public List<int> BatchSizes { get; } = [];
        public SemaphoreSlim LockAttempts { get; } = new(0);
        public SemaphoreSlim DeleteStarted { get; } = new(0);
        public int LocksReleased { get; private set; }

        public Task<IAsyncDisposable?> TryAcquireLockAsync(CancellationToken cancellationToken)
        {
            LockAttempts.Release();
            if (LockFailure is not null)
                throw LockFailure;
            return Task.FromResult<IAsyncDisposable?>(LockAvailable ? new Lease(this) : null);
        }

        public async Task<int> DeleteBatchAsync(RetentionPass pass, int batchSize, CancellationToken cancellationToken)
        {
            Calls.Add(pass);
            BatchSizes.Add(batchSize);
            DeleteStarted.Release();
            if (DeleteFailure is not null)
                throw DeleteFailure;
            if (BlockDeletes)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            return Results.TryGetValue(pass, out var queue) && queue.Count > 0 ? queue.Dequeue() : 0;
        }

        private sealed class Lease(FakeStore store) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                store.LocksReleased++;
                return ValueTask.CompletedTask;
            }
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test tests/Vestige.Tests -- --filter-class "Vestige.Tests.RetentionLoopTests"`
Expected: the build FAILS with `CS0246: The type or namespace name 'IRetentionStore' could not be found`.

- [ ] **Step 5: Implement**

`src/Vestige/Retention/IRetentionStore.cs`:

```csharp
namespace Vestige.Retention;

/// <summary>Storage-specific half of retention: a cross-instance lock and one batched delete.</summary>
public interface IRetentionStore
{
    /// <summary>
    /// Try to become the single instance purging this table. Returns a lease that releases the
    /// lock when disposed, or <c>null</c> when another instance holds it.
    /// </summary>
    Task<IAsyncDisposable?> TryAcquireLockAsync(CancellationToken cancellationToken);

    /// <summary>Delete at most <paramref name="batchSize"/> rows matching <paramref name="pass"/>; return rows deleted.</summary>
    Task<int> DeleteBatchAsync(RetentionPass pass, int batchSize, CancellationToken cancellationToken);
}
```

`src/Vestige/Retention/RetentionCycleResult.cs`:

```csharp
namespace Vestige.Retention;

/// <summary>Outcome of one retention cycle.</summary>
public sealed record RetentionCycleResult(bool Acquired, IReadOnlyList<long> DeletedPerPass)
{
    /// <summary>The cycle did not run because another instance held the lock.</summary>
    public static RetentionCycleResult Skipped { get; } = new(false, []);

    /// <summary>Rows deleted across all passes.</summary>
    public long TotalDeleted => DeletedPerPass.Sum();
}
```

`src/Vestige/Retention/RetentionLoop.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace Vestige.Retention;

/// <summary>Runs retention passes against a store on a fixed schedule.</summary>
public sealed class RetentionLoop
{
    /// <summary>Rows per delete; below SQL Server's 5000-lock escalation threshold.</summary>
    public const int BatchSize = 4000;

    /// <summary>Pause between full batches so purges never monopolise the table.</summary>
    public static readonly TimeSpan BatchPause = TimeSpan.FromMilliseconds(100);

    /// <summary>Delay before the first cycle after startup.</summary>
    public static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);

    /// <summary>Delay between cycles.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IReadOnlyList<RetentionPass> _passes;
    private readonly IRetentionStore _store;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    public RetentionLoop(
        IReadOnlyList<RetentionPass> passes,
        IRetentionStore store,
        ILogger logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(passes);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _passes = passes;
        _store = store;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <summary>Run cycles until <paramref name="cancellationToken"/> is cancelled. Never throws on cancellation.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(InitialDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                try
                {
                    await RunCycleAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Vestige retention cycle failed.");
                }

                await Task.Delay(Interval, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Run one cycle now: take the lock, then every pass in order.</summary>
    public async Task<RetentionCycleResult> RunCycleAsync(CancellationToken cancellationToken)
    {
        await using var lease = await _store.TryAcquireLockAsync(cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            _logger.LogDebug("Vestige retention skipped: another instance holds the retention lock.");
            return RetentionCycleResult.Skipped;
        }

        var started = _timeProvider.GetTimestamp();
        var deleted = new long[_passes.Count];

        for (var i = 0; i < _passes.Count; i++)
        {
            while (true)
            {
                var rows = await _store.DeleteBatchAsync(_passes[i], BatchSize, cancellationToken).ConfigureAwait(false);
                deleted[i] += rows;
                if (rows < BatchSize)
                    break;

                await Task.Delay(BatchPause, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }

        var total = deleted.Sum();
        if (total > 0)
        {
            _logger.LogInformation(
                "Vestige retention deleted {Deleted} rows ({PerPass}) in {ElapsedMs} ms.",
                total,
                string.Join(", ", _passes.Select((p, i) => $"{p.Period.TotalDays:0.##}d: {deleted[i]}")),
                (long)_timeProvider.GetElapsedTime(started).TotalMilliseconds);
        }

        return new RetentionCycleResult(true, deleted);
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Vestige.Tests -- --filter-class "Vestige.Tests.RetentionLoopTests"`
Expected: PASS (8 tests).

- [ ] **Step 7: Commit**

```bash
git add Directory.Packages.props tests/Vestige.Tests/Vestige.Tests.csproj src/Vestige/Retention tests/Vestige.Tests/TestSupport/ListLogger.cs tests/Vestige.Tests/RetentionLoopTests.cs
git commit -m "feat(retention): add scheduled retention loop over pluggable stores"
```

---

### Task 3: SQL Server promoted-field model and options validation

**Files:**
- Create: `src/Vestige.Sinks.SqlServer/PromotedType.cs`, `src/Vestige.Sinks.SqlServer/PromotedField.cs`, `src/Vestige.Sinks.SqlServer/PromotedFieldCollection.cs`, `src/Vestige.Sinks.SqlServer/SqlServerSinkOptionsValidator.cs`, `src/Vestige.Sinks.SqlServer/Properties/AssemblyInfo.cs`
- Modify: `src/Vestige.Sinks.SqlServer/SqlServerSinkOptions.cs`, `src/Vestige/Properties/AssemblyInfo.cs`
- Test: `tests/Vestige.Tests/PromotedFieldTests.cs`

**Interfaces:**
- Consumes: `RetentionOptions` (Task 1).
- Produces:
  - `public sealed class PromotedType`:
    - Public: `String(int)`, `Guid`, `Int`, `Long`, `Bool`, `DateTimeOffset`.
    - Internal: `PromotedKind Kind`, `int Length`, `string SqlTypeName`, `SqlDbType SqlDbType`, `Type ClrType`, `bool Matches(string typeName, short maxLength, byte scale)`.
  - `internal enum PromotedKind { String, Guid, Int, Long, Bool, DateTimeOffset }`
  - `public sealed record PromotedField(string Key, PromotedType Type, string Column, bool Indexed)`
  - `public sealed class PromotedFieldCollection : IReadOnlyList<PromotedField>`:
    - `Add(string key, PromotedType type, string? column = null, bool indexed = true)`
    - `internal static string DefaultColumnName(string key)`
  - `SqlServerSinkOptions.PromotedFields` and `SqlServerSinkOptions.Retention` (`RetentionOptions`)
  - `internal static SqlServerSinkOptionsValidator.Validate(SqlServerSinkOptions)` and `internal static IReadOnlySet<string> ReservedColumns` (case-insensitive)

- [ ] **Step 1: Expose internals to test projects**

Create `src/Vestige.Sinks.SqlServer/Properties/AssemblyInfo.cs`:

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Vestige.Tests")]
[assembly: InternalsVisibleTo("Vestige.Sinks.IntegrationTests")]
```

Replace `src/Vestige/Properties/AssemblyInfo.cs` with:

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Vestige.Tests")]
[assembly: InternalsVisibleTo("Vestige.Sinks.IntegrationTests")]
```

- [ ] **Step 2: Write the failing tests**

`tests/Vestige.Tests/PromotedFieldTests.cs`:

```csharp
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
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Vestige.Tests -- --filter-class "Vestige.Tests.PromotedFieldTests"`
Expected: the build FAILS with `CS0246: The type or namespace name 'PromotedFieldCollection' could not be found`.

- [ ] **Step 4: Implement**

`src/Vestige.Sinks.SqlServer/PromotedType.cs`:

```csharp
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
```

`src/Vestige.Sinks.SqlServer/PromotedField.cs`:

```csharp
namespace Vestige.Sinks.SqlServer;

/// <summary>A wide-event field copied into its own typed column.</summary>
/// <param name="Key">Field name exactly as passed to <c>WideEvent.Set</c> (ordinal, case-sensitive).</param>
/// <param name="Type">Column type.</param>
/// <param name="Column">Column name.</param>
/// <param name="Indexed">Create <c>([Column], [timestamp_utc] DESC)</c>.</param>
public sealed record PromotedField(string Key, PromotedType Type, string Column, bool Indexed);
```

`src/Vestige.Sinks.SqlServer/PromotedFieldCollection.cs`:

```csharp
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
```

`src/Vestige.Sinks.SqlServer/SqlServerSinkOptions.cs`:

```csharp
using Vestige.Retention;

namespace Vestige.Sinks.SqlServer;

public sealed class SqlServerSinkOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public string Schema { get; set; } = "dbo";
    public string Table { get; set; } = "wide_events";
    public bool AutoCreate { get; set; } = true;

    /// <summary>Fields stored in their own typed columns so they can be indexed and filtered cheaply.</summary>
    public PromotedFieldCollection PromotedFields { get; } = new();

    /// <summary>Tiered retention. Off until <see cref="RetentionOptions.Default"/> is set.</summary>
    public RetentionOptions Retention { get; } = new();
}
```

`src/Vestige.Sinks.SqlServer/SqlServerSinkOptionsValidator.cs`:

```csharp
namespace Vestige.Sinks.SqlServer;

internal static class SqlServerSinkOptionsValidator
{
    internal static readonly IReadOnlySet<string> ReservedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "id", "event_id", "timestamp_utc", "service_name", "service_version", "deployment_environment",
        "cloud_region", "trace_id", "span_id", "outcome", "duration_ms", "http_status_code",
        "http_method", "http_path", "error_type", "payload",
    };

    public static void Validate(SqlServerSinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in options.PromotedFields)
        {
            if (!keys.Add(field.Key))
                throw new InvalidOperationException($"Field '{field.Key}' is promoted more than once.");

            ValidateColumnName(field);

            if (ReservedColumns.Contains(field.Column))
                throw new InvalidOperationException(
                    $"Promoted field '{field.Key}' maps to column [{field.Column}], which is reserved by the sink. Pass column: to choose another name.");

            if (!columns.Add(field.Column))
                throw new InvalidOperationException(
                    $"Column [{field.Column}] is used by more than one promoted field.");
        }

        options.Retention.Validate();

        foreach (var rule in options.Retention.Rules)
        {
            if (!keys.Contains(rule.Key))
                throw new InvalidOperationException($"retention rule on '{rule.Key}' requires a promoted field");
        }
    }

    private static void ValidateColumnName(PromotedField field)
    {
        var column = field.Column;
        if (column.Length is 0 or > 128)
            throw new InvalidOperationException(
                $"Promoted field '{field.Key}' column name must be 1 to 128 characters.");

        var first = column[0];
        var validFirst = char.IsAsciiLetter(first) || first == '_';
        var validRest = column.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
        if (!validFirst || !validRest)
            throw new InvalidOperationException(
                $"Promoted field '{field.Key}' column [{column}] must start with a letter or underscore and contain only letters, digits and underscores.");
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Vestige.Tests -- --filter-class "Vestige.Tests.PromotedFieldTests"`
Expected: PASS (22 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Vestige/Properties/AssemblyInfo.cs src/Vestige.Sinks.SqlServer tests/Vestige.Tests/PromotedFieldTests.cs
git commit -m "feat(sqlserver): add promoted field model and options validation"
```

---

### Task 4: Promoted value conversion and row mapping

**Files:**
- Create: `src/Vestige.Sinks.SqlServer/PromotedValueConverter.cs`, `src/Vestige.Sinks.SqlServer/PromotionWarningLimiter.cs`
- Modify: `src/Vestige.Sinks.SqlServer/SqlServerEventRow.cs`
- Test: `tests/Vestige.Tests/PromotedValueConverterTests.cs`, `tests/Vestige.Tests/SqlServerEventRowTests.cs`

**Interfaces:**
- Consumes: `PromotedType`, `PromotedKind`, `PromotedField` (Task 3); `ListLogger<T>` (Task 2).
- Produces:
  - `internal static bool PromotedValueConverter.TryConvert(PromotedType type, object value, out object? converted)`
  - `internal sealed class PromotionWarningLimiter(ILogger logger, TimeProvider timeProvider)` with `void Report(PromotedField field, object value)`
  - `SqlServerEventRow.PromotedValues` (`object?[]`, in `PromotedFields` order)
  - `SqlServerEventRow.Map(WideEventData data, IReadOnlyList<PromotedField> promoted, PromotionWarningLimiter? warnings)`; `Map(data)` is kept and maps with no promoted fields
  - `internal static string? SqlServerEventRow.Truncate(string?, int)`

- [ ] **Step 1: Write the failing tests**

`tests/Vestige.Tests/PromotedValueConverterTests.cs`:

```csharp
using Vestige.Sinks.SqlServer;

namespace Vestige.Tests;

public sealed class PromotedValueConverterTests
{
    private static readonly Guid SampleGuid = Guid.Parse("6f1c2a5e-1d2b-4c3d-9e8f-0a1b2c3d4e5f");

    public static TheoryData<PromotedType, object, object?> Convertible => new()
    {
        { PromotedType.String(10), "tenant.create", "tenant.create" },
        { PromotedType.String(5), "abcdefgh", "abcde" },
        { PromotedType.String(2), "a\U0001F600", "a" },
        { PromotedType.String(10), 42, "42" },
        { PromotedType.String(10), 1.5, "1.5" },
        { PromotedType.Guid, SampleGuid, SampleGuid },
        { PromotedType.Guid, SampleGuid.ToString(), SampleGuid },
        { PromotedType.Guid, "{" + SampleGuid + "}", SampleGuid },
        { PromotedType.Int, 7, 7 },
        { PromotedType.Int, (short)7, 7 },
        { PromotedType.Int, (byte)7, 7 },
        { PromotedType.Int, 7L, 7 },
        { PromotedType.Long, 7, 7L },
        { PromotedType.Long, 7L, 7L },
        { PromotedType.Bool, true, true },
        { PromotedType.DateTimeOffset, new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero) },
        { PromotedType.DateTimeOffset, "2026-10-16T12:00:00.0000000+00:00", new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero) },
        { PromotedType.DateTimeOffset, new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc), new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero) },
    };

    public static TheoryData<PromotedType, object> NotConvertible => new()
    {
        { PromotedType.Guid, "not-a-guid" },
        { PromotedType.Guid, 12 },
        { PromotedType.Int, (long)int.MaxValue + 1 },
        { PromotedType.Int, "7" },
        { PromotedType.Long, 1.5 },
        { PromotedType.Bool, "true" },
        { PromotedType.DateTimeOffset, "yesterday" },
    };

    [Theory]
    [MemberData(nameof(Convertible))]
    public void TryConvert_SupportedValues(PromotedType type, object value, object? expected)
    {
        Assert.True(PromotedValueConverter.TryConvert(type, value, out var converted));
        Assert.Equal(expected, converted);
    }

    [Theory]
    [MemberData(nameof(NotConvertible))]
    public void TryConvert_UnsupportedValues_ReturnsFalse(PromotedType type, object value)
    {
        Assert.False(PromotedValueConverter.TryConvert(type, value, out var converted));
        Assert.Null(converted);
    }
}
```

`tests/Vestige.Tests/SqlServerEventRowTests.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Vestige.Internal;
using Vestige.Sinks.SqlServer;
using Vestige.Tests.TestSupport;

namespace Vestige.Tests;

public sealed class SqlServerEventRowTests
{
    private static readonly Guid TenantId = Guid.Parse("6f1c2a5e-1d2b-4c3d-9e8f-0a1b2c3d4e5f");

    private static readonly PromotedField[] Promoted =
    [
        new("audit_action", PromotedType.String(128), "audit_action", true),
        new("tenant_id", PromotedType.Guid, "tenant_id", true),
        new("actor_user_id", PromotedType.Guid, "actor_user_id", true),
    ];

    private static WideEventData MakeData(Action<WideEvent> configure)
    {
        var ev = new WideEvent { ServiceName = "client-manager", DurationMs = 5, Outcome = "success" };
        configure(ev);
        return new SystemTextJsonSerializer().Serialize(ev);
    }

    [Fact]
    public void Map_WithoutPromotedFields_HasEmptyPromotedValues()
    {
        var row = SqlServerEventRow.Map(MakeData(_ => { }));

        Assert.Empty(row.PromotedValues);
    }

    [Fact]
    public void Map_PromotedFields_ConvertedInDeclaredOrder()
    {
        var data = MakeData(ev =>
        {
            ev.Set("tenant_id", TenantId);
            ev.Set("audit_action", "tenant.create");
            ev.Set("actor_user_id", TenantId.ToString());
        });

        var row = SqlServerEventRow.Map(data, Promoted, warnings: null);

        Assert.Equal(new object?[] { "tenant.create", TenantId, TenantId }, row.PromotedValues);
    }

    [Fact]
    public void Map_AbsentOrExplicitNullField_WritesNullWithoutWarning()
    {
        var logger = new ListLogger<SqlServerSink>();
        var warnings = new PromotionWarningLimiter(logger, new FakeTimeProvider());
        var data = MakeData(ev => ev.Set("tenant_id", null));

        var row = SqlServerEventRow.Map(data, Promoted, warnings);

        Assert.Equal(new object?[] { null, null, null }, row.PromotedValues);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Map_UnconvertibleValue_WritesNullAndWarnsOncePerMinuteWithoutLeakingValue()
    {
        var logger = new ListLogger<SqlServerSink>();
        var time = new FakeTimeProvider();
        var warnings = new PromotionWarningLimiter(logger, time);
        var data = MakeData(ev => ev.Set("tenant_id", "secret-not-a-guid"));

        var first = SqlServerEventRow.Map(data, Promoted, warnings);
        SqlServerEventRow.Map(data, Promoted, warnings);
        time.Advance(TimeSpan.FromSeconds(61));
        SqlServerEventRow.Map(data, Promoted, warnings);

        Assert.Null(first.PromotedValues[1]);
        Assert.Equal(2, logger.Entries.Count(e => e.Level == LogLevel.Warning));
        Assert.All(logger.Entries, e =>
        {
            Assert.Contains("tenant_id", e.Message);
            Assert.DoesNotContain("secret-not-a-guid", e.Message);
        });
    }

    [Fact]
    public void Map_UnconvertibleValue_KeepsOriginalInPayload()
    {
        var data = MakeData(ev => ev.Set("tenant_id", "not-a-guid"));

        var row = SqlServerEventRow.Map(data, Promoted, warnings: null);

        using var payload = System.Text.Json.JsonDocument.Parse(row.PayloadJson);
        Assert.Equal("not-a-guid", payload.RootElement.GetProperty("tenant_id").GetString());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Vestige.Tests -- --filter-class "Vestige.Tests.PromotedValueConverterTests" --filter-class "Vestige.Tests.SqlServerEventRowTests"`
Expected: the build FAILS with `CS0103: The name 'PromotedValueConverter' does not exist in the current context`.

- [ ] **Step 3: Implement**

`src/Vestige.Sinks.SqlServer/PromotedValueConverter.cs`:

```csharp
using System.Globalization;

namespace Vestige.Sinks.SqlServer;

internal static class PromotedValueConverter
{
    /// <summary>Convert a non-null field value to the promoted column's CLR type; false when it cannot be represented.</summary>
    public static bool TryConvert(PromotedType type, object value, out object? converted)
    {
        converted = type.Kind switch
        {
            PromotedKind.String => SqlServerEventRow.Truncate(
                value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture),
                type.Length),
            PromotedKind.Guid => value switch
            {
                Guid g => g,
                string s when Guid.TryParse(s, out var parsed) => parsed,
                _ => null,
            },
            PromotedKind.Int => value switch
            {
                int i => i,
                short s => (int)s,
                byte b => (int)b,
                long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
                _ => null,
            },
            PromotedKind.Long => value switch
            {
                long l => l,
                int i => (long)i,
                short s => (long)s,
                byte b => (long)b,
                _ => null,
            },
            PromotedKind.Bool => value as bool?,
            PromotedKind.DateTimeOffset => value switch
            {
                DateTimeOffset dto => dto,
                DateTime dt => new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero),
                string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) => parsed,
                _ => null,
            },
            _ => null,
        };

        return converted is not null;
    }
}
```

`src/Vestige.Sinks.SqlServer/PromotionWarningLimiter.cs`:

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Vestige.Sinks.SqlServer;

/// <summary>Logs a failed promotion at most once per field per minute. Never logs the value itself.</summary>
internal sealed class PromotionWarningLimiter(ILogger logger, TimeProvider timeProvider)
{
    private static readonly long WindowTicks = TimeSpan.FromMinutes(1).Ticks;
    private readonly ConcurrentDictionary<string, long> _lastWarnedTicks = new(StringComparer.Ordinal);

    public void Report(PromotedField field, object value)
    {
        var now = timeProvider.GetUtcNow().UtcTicks;
        var last = _lastWarnedTicks.GetOrAdd(field.Key, long.MinValue);
        if (last != long.MinValue && now - last < WindowTicks)
            return;
        if (!_lastWarnedTicks.TryUpdate(field.Key, now, last))
            return;

        logger.LogWarning(
            "Vestige could not convert field '{Field}' of type {ValueType} to promoted column [{Column}] {SqlType}; wrote NULL. The value is still in payload.",
            field.Key,
            value.GetType().Name,
            field.Column,
            field.Type.SqlTypeName);
    }
}
```

Edit `src/Vestige.Sinks.SqlServer/SqlServerEventRow.cs`:

1. Add this property after `PayloadJson`:

```csharp
    /// <summary>Promoted column values in <see cref="SqlServerSinkOptions.PromotedFields"/> order; null means SQL NULL.</summary>
    public object?[] PromotedValues { get; init; } = [];
```

2. Replace the existing `public static SqlServerEventRow Map(WideEventData data)` method header and body opening with the two overloads below. Keep the existing initializer lines and add `PromotedValues`:

```csharp
    public static SqlServerEventRow Map(WideEventData data) => Map(data, [], null);

    public static SqlServerEventRow Map(
        WideEventData data,
        IReadOnlyList<PromotedField> promoted,
        PromotionWarningLimiter? warnings)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(promoted);

        var eventId = GetRequiredString(data.Fields, "event_id");
        if (eventId.Length > 256)
            throw new ArgumentOutOfRangeException(nameof(eventId), "Event id cannot exceed 256 characters.");

        return new SqlServerEventRow
        {
            EventId = eventId,
            TimestampUtc = GetRequiredTimestamp(data.Fields, "timestamp"),
            ServiceName = Truncate(GetOptionalString(data.Fields, "service.name"), 256),
            ServiceVersion = Truncate(GetOptionalString(data.Fields, "service.version"), 64),
            DeploymentEnvironment = Truncate(GetOptionalString(data.Fields, "deployment.environment"), 128),
            CloudRegion = Truncate(GetOptionalString(data.Fields, "cloud.region"), 128),
            TraceId = Truncate(GetOptionalString(data.Fields, "trace_id"), 64),
            SpanId = Truncate(GetOptionalString(data.Fields, "span_id"), 32),
            Outcome = Truncate(GetOptionalString(data.Fields, "outcome"), 32),
            DurationMs = GetRequiredDouble(data.Fields, "duration_ms"),
            HttpStatusCode = GetOptionalInt(data.Fields, "http.status_code"),
            HttpMethod = Truncate(GetOptionalString(data.Fields, "http.method"), 16),
            HttpPath = Truncate(GetOptionalString(data.Fields, "http.path"), 2048),
            ErrorType = Truncate(GetOptionalString(data.Fields, "error.type"), 512),
            PayloadJson = Encoding.UTF8.GetString(data.JsonBytes),
            PromotedValues = MapPromoted(data.Fields, promoted, warnings),
        };
    }

    private static object?[] MapPromoted(
        IReadOnlyDictionary<string, object?> fields,
        IReadOnlyList<PromotedField> promoted,
        PromotionWarningLimiter? warnings)
    {
        if (promoted.Count == 0)
            return [];

        var values = new object?[promoted.Count];
        for (var i = 0; i < promoted.Count; i++)
        {
            var field = promoted[i];
            if (!fields.TryGetValue(field.Key, out var raw) || raw is null)
                continue;

            if (PromotedValueConverter.TryConvert(field.Type, raw, out var converted))
                values[i] = converted;
            else
                warnings?.Report(field, raw);
        }

        return values;
    }
```

3. Change `private static string? Truncate(string? value, int maxLength)` to `internal static string? Truncate(string? value, int maxLength)`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Vestige.Tests -- --filter-class "Vestige.Tests.PromotedValueConverterTests" --filter-class "Vestige.Tests.SqlServerEventRowTests"`
Expected: PASS, 0 failed. Theory rows built from `PromotedType` may be reported as a single test case each, because it isn't serializable.

- [ ] **Step 5: Commit**

```bash
git add src/Vestige.Sinks.SqlServer tests/Vestige.Tests/PromotedValueConverterTests.cs tests/Vestige.Tests/SqlServerEventRowTests.cs
git commit -m "feat(sqlserver): convert promoted field values with rate-limited warnings"
```

---

### Task 5: Table layout and layout-driven writes

**Files:**
- Create: `src/Vestige.Sinks.SqlServer/SqlServerTableLayout.cs`, `tests/Vestige.Tests/TestSupport/FakeSqlServerWriter.cs`
- Modify: `src/Vestige.Sinks.SqlServer/ISqlServerWriter.cs`, `src/Vestige.Sinks.SqlServer/SqlServerWriter.cs`, `src/Vestige.Sinks.SqlServer/SqlServerSink.cs`
- Test: `tests/Vestige.Tests/SqlServerTableLayoutTests.cs`, `tests/Vestige.Tests/SqlServerSinkTests.cs`

**Interfaces:**
- Consumes: `SqlServerSinkOptions`, `PromotedField`, `SqlServerSinkOptionsValidator` (Task 3); `SqlServerEventRow.Map(data, promoted, warnings)`, `PromotionWarningLimiter` (Task 4).
- Produces:
  - `internal sealed class SqlServerTableLayout(SqlServerSinkOptions)`:
    - Names: `QualifiedTable` (`[schema].[table]`), `QualifiedTableLiteral` (quote-escaped for `N'...'`), `IndexPrefix`, `RetentionLockName`.
    - Columns and SQL: `Promoted`, `InsertColumns`, `InsertSql`.
    - Methods: `IndexName(PromotedField)`, `ColumnFor(string key)`, `DataTable CreateDataTable()`, `void AddRow(DataTable, SqlServerEventRow)`, `void AddPromotedParameters(SqlCommand, SqlServerEventRow)`, `static string QuoteIdentifier(string)`, `static string Literal(string)`.
  - New `ISqlServerWriter` shape, used unchanged by all later tasks:

    ```csharp
    Task InitializeAsync(SqlServerSinkOptions options, SqlServerTableLayout layout, CancellationToken cancellationToken);
    Task<IReadOnlyList<PromotedField>> EnsureStorageAsync(CancellationToken cancellationToken); // promoted indexes still to build
    Task VerifyStorageAsync(CancellationToken cancellationToken);
    Task WriteSingleAsync(SqlServerEventRow row, CancellationToken cancellationToken);
    Task WriteAsync(IReadOnlyList<SqlServerEventRow> batch, CancellationToken cancellationToken);
    ```

  - `SqlServerSink` constructors:
    - `public (IOptions<SqlServerSinkOptions>)`
    - `public (IOptions<SqlServerSinkOptions>, ILogger<SqlServerSink>)`
    - `internal (IOptions<SqlServerSinkOptions>, ILogger, ISqlServerWriter, TimeProvider)`
  - Test helper `FakeSqlServerWriter` (records `Layout`, `Inserts`, `Batches`, `EnsureCalls`, `VerifyCalls`; settable `PendingIndexes`).

- [ ] **Step 1: Write the failing tests**

`tests/Vestige.Tests/SqlServerTableLayoutTests.cs`:

```csharp
using System.Data;
using Microsoft.Data.SqlClient;
using Vestige.Sinks.SqlServer;

namespace Vestige.Tests;

public sealed class SqlServerTableLayoutTests
{
    private const string FixedInsert020 =
        "INSERT INTO [dbo].[wide_events] " +
        "(event_id, timestamp_utc, service_name, service_version, deployment_environment, cloud_region, trace_id, span_id, outcome, duration_ms, http_status_code, http_method, http_path, error_type, payload) " +
        "VALUES (@event_id, @timestamp_utc, @service_name, @service_version, @deployment_environment, @cloud_region, @trace_id, @span_id, @outcome, @duration_ms, @http_status_code, @http_method, @http_path, @error_type, @payload)";

    private static SqlServerSinkOptions Options(Action<SqlServerSinkOptions>? configure = null)
    {
        var options = new SqlServerSinkOptions { ConnectionString = "Server=.;Database=x" };
        configure?.Invoke(options);
        return options;
    }

    [Fact]
    public void InsertSql_WithoutPromotedFields_IsIdenticalTo020()
    {
        var layout = new SqlServerTableLayout(Options());

        Assert.Equal(FixedInsert020, layout.InsertSql);
        Assert.Equal(15, layout.InsertColumns.Count);
    }

    [Fact]
    public void InsertSql_WithPromotedFields_AppendsQuotedColumnsAndIndexedParameters()
    {
        var layout = new SqlServerTableLayout(Options(o =>
        {
            o.PromotedFields.Add("audit_action", PromotedType.String(128));
            o.PromotedFields.Add("tenant_id", PromotedType.Guid);
        }));

        Assert.EndsWith(
            "error_type, payload, [audit_action], [tenant_id]) VALUES (@event_id, @timestamp_utc, @service_name, @service_version, @deployment_environment, @cloud_region, @trace_id, @span_id, @outcome, @duration_ms, @http_status_code, @http_method, @http_path, @error_type, @payload, @promoted_0, @promoted_1)",
            layout.InsertSql);
        Assert.Equal(new[] { "audit_action", "tenant_id" }, layout.InsertColumns.TakeLast(2));
    }

    [Fact]
    public void Names_SanitizeAndQuoteUnusualTableNames()
    {
        var layout = new SqlServerTableLayout(Options(o =>
        {
            o.Schema = "ops";
            o.Table = "wide.events]x'y";
            o.PromotedFields.Add("audit_action", PromotedType.String(128));
        }));

        Assert.Equal("[ops].[wide.events]]x'y]", layout.QualifiedTable);
        Assert.Equal("[ops].[wide.events]]x''y]", layout.QualifiedTableLiteral);
        Assert.Equal("ops_wide_events]x'y", layout.IndexPrefix);
        Assert.Equal("ops_wide_events]x'y_audit_action_idx", layout.IndexName(layout.Promoted[0]));
        Assert.Equal("ops_wide_events]x'y_retention", layout.RetentionLockName);
    }

    [Fact]
    public void ColumnFor_ReturnsColumnOfPromotedKey()
    {
        var layout = new SqlServerTableLayout(Options(o => o.PromotedFields.Add("user.id", PromotedType.String(64))));

        Assert.Equal("user_id", layout.ColumnFor("user.id"));
        Assert.Throws<KeyNotFoundException>(() => layout.ColumnFor("missing"));
    }

    [Fact]
    public void DataTable_HasFixedThenPromotedColumns_AndRowsUseDbNull()
    {
        var tenant = Guid.NewGuid();
        var layout = new SqlServerTableLayout(Options(o =>
        {
            o.PromotedFields.Add("audit_action", PromotedType.String(128));
            o.PromotedFields.Add("tenant_id", PromotedType.Guid);
        }));
        var row = new SqlServerEventRow
        {
            EventId = "e1",
            TimestampUtc = DateTimeOffset.UtcNow,
            DurationMs = 1,
            PayloadJson = "{}",
            PromotedValues = [null, tenant],
        };

        using var table = layout.CreateDataTable();
        layout.AddRow(table, row);

        Assert.Equal(layout.InsertColumns, table.Columns.Cast<DataColumn>().Select(c => c.ColumnName));
        Assert.Equal(typeof(Guid), table.Columns["tenant_id"]!.DataType);
        Assert.Equal(DBNull.Value, table.Rows[0]["audit_action"]);
        Assert.Equal(tenant, table.Rows[0]["tenant_id"]);
    }

    [Fact]
    public void AddPromotedParameters_TypesAndSizesParameters()
    {
        var layout = new SqlServerTableLayout(Options(o =>
        {
            o.PromotedFields.Add("audit_action", PromotedType.String(128));
            o.PromotedFields.Add("tenant_id", PromotedType.Guid);
        }));
        var row = new SqlServerEventRow
        {
            EventId = "e1",
            TimestampUtc = DateTimeOffset.UtcNow,
            DurationMs = 1,
            PayloadJson = "{}",
            PromotedValues = ["tenant.create", null],
        };
        using var command = new SqlCommand();

        layout.AddPromotedParameters(command, row);

        Assert.Equal(SqlDbType.NVarChar, command.Parameters["@promoted_0"].SqlDbType);
        Assert.Equal(128, command.Parameters["@promoted_0"].Size);
        Assert.Equal("tenant.create", command.Parameters["@promoted_0"].Value);
        Assert.Equal(SqlDbType.UniqueIdentifier, command.Parameters["@promoted_1"].SqlDbType);
        Assert.Equal(DBNull.Value, command.Parameters["@promoted_1"].Value);
    }
}
```

`tests/Vestige.Tests/TestSupport/FakeSqlServerWriter.cs`:

```csharp
using System.Collections.Concurrent;
using Vestige.Sinks.SqlServer;

namespace Vestige.Tests.TestSupport;

internal sealed class FakeSqlServerWriter : ISqlServerWriter
{
    private readonly ConcurrentQueue<SqlServerEventRow> _inserts = new();
    private readonly ConcurrentQueue<IReadOnlyList<SqlServerEventRow>> _batches = new();

    public SqlServerTableLayout? Layout { get; private set; }
    public IReadOnlyList<PromotedField> PendingIndexes { get; set; } = [];
    public int EnsureCalls { get; private set; }
    public int VerifyCalls { get; private set; }
    public bool Disposed { get; private set; }
    public IReadOnlyCollection<SqlServerEventRow> Inserts => _inserts.ToArray();
    public IReadOnlyCollection<IReadOnlyList<SqlServerEventRow>> Batches => _batches.ToArray();

    public Task InitializeAsync(SqlServerSinkOptions options, SqlServerTableLayout layout, CancellationToken cancellationToken)
    {
        Layout = layout;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PromotedField>> EnsureStorageAsync(CancellationToken cancellationToken)
    {
        EnsureCalls++;
        return Task.FromResult(PendingIndexes);
    }

    public Task VerifyStorageAsync(CancellationToken cancellationToken)
    {
        VerifyCalls++;
        return Task.CompletedTask;
    }

    public Task WriteSingleAsync(SqlServerEventRow row, CancellationToken cancellationToken)
    {
        _inserts.Enqueue(row);
        return Task.CompletedTask;
    }

    public Task WriteAsync(IReadOnlyList<SqlServerEventRow> batch, CancellationToken cancellationToken)
    {
        _batches.Enqueue(batch.ToArray());
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
```

`tests/Vestige.Tests/SqlServerSinkTests.cs`:

```csharp
using Microsoft.Extensions.Options;
using Vestige.Internal;
using Vestige.Sinks.SqlServer;
using Vestige.Tests.TestSupport;

namespace Vestige.Tests;

public sealed class SqlServerSinkTests
{
    private static WideEventData MakeData(Action<WideEvent>? configure = null)
    {
        var ev = new WideEvent { ServiceName = "client-manager", DurationMs = 3, Outcome = "success" };
        configure?.Invoke(ev);
        return new SystemTextJsonSerializer().Serialize(ev);
    }

    private static SqlServerSink CreateSink(
        FakeSqlServerWriter writer,
        Action<SqlServerSinkOptions>? configure = null,
        ListLogger<SqlServerSink>? logger = null)
    {
        var options = new SqlServerSinkOptions { ConnectionString = "Server=.;Database=x" };
        configure?.Invoke(options);
        return new SqlServerSink(Options.Create(options), logger ?? new ListLogger<SqlServerSink>(), writer, TimeProvider.System);
    }

    [Fact]
    public async Task Initialize_PassesLayoutAndEnsuresStorage()
    {
        var writer = new FakeSqlServerWriter();
        await using var sink = CreateSink(writer, o => o.PromotedFields.Add("audit_action", PromotedType.String(128)));

        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal("audit_action", Assert.Single(writer.Layout!.Promoted).Column);
        Assert.Equal(1, writer.EnsureCalls);
        Assert.Equal(0, writer.VerifyCalls);
    }

    [Fact]
    public async Task Initialize_AutoCreateDisabled_VerifiesInsteadOfEnsuring()
    {
        var writer = new FakeSqlServerWriter();
        await using var sink = CreateSink(writer, o => o.AutoCreate = false);

        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, writer.EnsureCalls);
        Assert.Equal(1, writer.VerifyCalls);
    }

    [Fact]
    public async Task Initialize_InvalidPromotedFields_Throws()
    {
        var writer = new FakeSqlServerWriter();
        await using var sink = CreateSink(writer, o => o.PromotedFields.Add("http.method", PromotedType.String(16)));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.InitializeAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Emit_SingleAndBatch_CarryPromotedValues()
    {
        var writer = new FakeSqlServerWriter();
        await using var sink = CreateSink(writer, o => o.PromotedFields.Add("audit_action", PromotedType.String(128)));
        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        await sink.EmitAsync(MakeData(ev => ev.Set("audit_action", "tenant.create")), TestContext.Current.CancellationToken);
        await sink.EmitBatchAsync(
            [MakeData(ev => ev.Set("audit_action", "api_key.revoke")), MakeData()],
            TestContext.Current.CancellationToken);

        Assert.Equal("tenant.create", Assert.Single(writer.Inserts).PromotedValues[0]);
        var batch = Assert.Single(writer.Batches);
        Assert.Equal("api_key.revoke", batch[0].PromotedValues[0]);
        Assert.Null(batch[1].PromotedValues[0]);
    }

    [Fact]
    public async Task Emit_BeforeInitialize_Throws()
    {
        await using var sink = CreateSink(new FakeSqlServerWriter());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.EmitAsync(MakeData(), TestContext.Current.CancellationToken));
        Assert.Contains("must be initialized", ex.Message);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Vestige.Tests -- --filter-class "Vestige.Tests.SqlServerTableLayoutTests" --filter-class "Vestige.Tests.SqlServerSinkTests"`
Expected: the build FAILS with `CS0246: The type or namespace name 'SqlServerTableLayout' could not be found`.

- [ ] **Step 3: Implement the layout**

`src/Vestige.Sinks.SqlServer/SqlServerTableLayout.cs`:

```csharp
using System.Data;
using Microsoft.Data.SqlClient;

namespace Vestige.Sinks.SqlServer;

/// <summary>Everything about the table shape, computed once at initialization.</summary>
internal sealed class SqlServerTableLayout
{
    internal static readonly string[] FixedInsertColumns =
    [
        "event_id", "timestamp_utc", "service_name", "service_version", "deployment_environment", "cloud_region",
        "trace_id", "span_id", "outcome", "duration_ms", "http_status_code", "http_method", "http_path",
        "error_type", "payload",
    ];

    private static readonly Type[] FixedClrTypes =
    [
        typeof(string), typeof(DateTimeOffset), typeof(string), typeof(string), typeof(string), typeof(string),
        typeof(string), typeof(string), typeof(string), typeof(double), typeof(int), typeof(string), typeof(string),
        typeof(string), typeof(string),
    ];

    private readonly DataTable _template;
    private readonly Dictionary<string, string> _columnsByKey;

    public SqlServerTableLayout(SqlServerSinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Promoted = options.PromotedFields.ToArray();
        QualifiedTable = $"{QuoteIdentifier(options.Schema)}.{QuoteIdentifier(options.Table)}";
        QualifiedTableLiteral = Literal(QualifiedTable);
        IndexPrefix = $"{options.Schema}_{options.Table}".Replace(".", "_", StringComparison.Ordinal);
        RetentionLockName = $"{IndexPrefix}_retention";
        InsertColumns = [.. FixedInsertColumns, .. Promoted.Select(f => f.Column)];
        _columnsByKey = Promoted.ToDictionary(f => f.Key, f => f.Column, StringComparer.Ordinal);

        var columns = string.Join(", ", FixedInsertColumns.Concat(Promoted.Select(f => QuoteIdentifier(f.Column))));
        var values = string.Join(", ", FixedInsertColumns.Select(c => "@" + c).Concat(Promoted.Select((_, i) => $"@promoted_{i}")));
        InsertSql = $"INSERT INTO {QualifiedTable} ({columns}) VALUES ({values})";

        _template = new DataTable();
        for (var i = 0; i < FixedInsertColumns.Length; i++)
            _template.Columns.Add(FixedInsertColumns[i], FixedClrTypes[i]);
        foreach (var field in Promoted)
            _template.Columns.Add(field.Column, field.Type.ClrType);
    }

    public IReadOnlyList<PromotedField> Promoted { get; }

    /// <summary><c>[schema].[table]</c>.</summary>
    public string QualifiedTable { get; }

    /// <summary><see cref="QualifiedTable"/> escaped for use inside <c>N'...'</c>.</summary>
    public string QualifiedTableLiteral { get; }

    public string IndexPrefix { get; }

    public string RetentionLockName { get; }

    /// <summary>Insertable columns: fixed first, then promoted in declared order.</summary>
    public IReadOnlyList<string> InsertColumns { get; }

    public string InsertSql { get; }

    public string IndexName(PromotedField field) => $"{IndexPrefix}_{field.Column}_idx";

    public string ColumnFor(string key) => _columnsByKey[key];

    public DataTable CreateDataTable() => _template.Clone();

    public void AddRow(DataTable table, SqlServerEventRow row)
    {
        var values = new object[InsertColumns.Count];
        values[0] = row.EventId;
        values[1] = row.TimestampUtc;
        values[2] = (object?)row.ServiceName ?? DBNull.Value;
        values[3] = (object?)row.ServiceVersion ?? DBNull.Value;
        values[4] = (object?)row.DeploymentEnvironment ?? DBNull.Value;
        values[5] = (object?)row.CloudRegion ?? DBNull.Value;
        values[6] = (object?)row.TraceId ?? DBNull.Value;
        values[7] = (object?)row.SpanId ?? DBNull.Value;
        values[8] = (object?)row.Outcome ?? DBNull.Value;
        values[9] = row.DurationMs;
        values[10] = (object?)row.HttpStatusCode ?? DBNull.Value;
        values[11] = (object?)row.HttpMethod ?? DBNull.Value;
        values[12] = (object?)row.HttpPath ?? DBNull.Value;
        values[13] = (object?)row.ErrorType ?? DBNull.Value;
        values[14] = row.PayloadJson;
        for (var i = 0; i < Promoted.Count; i++)
            values[FixedInsertColumns.Length + i] = PromotedValueAt(row, i) ?? DBNull.Value;

        table.Rows.Add(values);
    }

    public void AddPromotedParameters(SqlCommand command, SqlServerEventRow row)
    {
        for (var i = 0; i < Promoted.Count; i++)
        {
            var field = Promoted[i];
            var parameter = new SqlParameter($"@promoted_{i}", field.Type.SqlDbType)
            {
                Value = PromotedValueAt(row, i) ?? DBNull.Value,
            };
            if (field.Type.Kind == PromotedKind.String)
                parameter.Size = field.Type.Length;
            command.Parameters.Add(parameter);
        }
    }

    public static string QuoteIdentifier(string identifier)
        => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    public static string Literal(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static object? PromotedValueAt(SqlServerEventRow row, int index)
        => index < row.PromotedValues.Length ? row.PromotedValues[index] : null;
}
```

- [ ] **Step 4: Change the writer contract and make writes use the layout**

Replace `src/Vestige.Sinks.SqlServer/ISqlServerWriter.cs`:

```csharp
namespace Vestige.Sinks.SqlServer;

internal interface ISqlServerWriter : IAsyncDisposable
{
    Task InitializeAsync(SqlServerSinkOptions options, SqlServerTableLayout layout, CancellationToken cancellationToken);

    /// <summary>Create or upgrade storage; returns promoted indexes that still need a background build.</summary>
    Task<IReadOnlyList<PromotedField>> EnsureStorageAsync(CancellationToken cancellationToken);

    /// <summary>With AutoCreate disabled: fail when promoted columns are missing or incompatible.</summary>
    Task VerifyStorageAsync(CancellationToken cancellationToken);

    Task WriteSingleAsync(SqlServerEventRow row, CancellationToken cancellationToken);

    Task WriteAsync(IReadOnlyList<SqlServerEventRow> batch, CancellationToken cancellationToken);
}
```

In `src/Vestige.Sinks.SqlServer/SqlServerWriter.cs`:

1. Add the field `private SqlServerTableLayout? _layout;` beside `_options`.
2. Replace `InitializeAsync` with:

```csharp
    public async Task InitializeAsync(SqlServerSinkOptions options, SqlServerTableLayout layout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(layout);
        ValidateOptions(options);

        _options = options;
        _layout = layout;
        _connection = new SqlConnection(options.ConnectionString);
        await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    }
```

3. Change the signature of `EnsureStorageAsync` to `public async Task<IReadOnlyList<PromotedField>> EnsureStorageAsync(CancellationToken cancellationToken)`. Keep its body unchanged for now and add `return [];` as its last statement. Task 7 replaces this body.
4. Add:

```csharp
    public Task VerifyStorageAsync(CancellationToken cancellationToken) => Task.CompletedTask;
```

5. In `WriteSingleAsync`, replace the `options`/`sql` local declarations with:

```csharp
        var layout = _layout ?? throw new InvalidOperationException("Sink layout has not been initialized.");
        var sql = layout.InsertSql;
```

Then, immediately before `await command.ExecuteNonQueryAsync(...)`, add:

```csharp
        layout.AddPromotedParameters(command, row);
```

6. Replace the whole body of `WriteAsync` after the `if (batch.Count == 0) return;` guard with:

```csharp
        var connection = await GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var layout = _layout ?? throw new InvalidOperationException("Sink layout has not been initialized.");

        using var bulkCopy = new SqlBulkCopy(connection);
        bulkCopy.DestinationTableName = layout.QualifiedTable;
        foreach (var column in layout.InsertColumns)
            bulkCopy.ColumnMappings.Add(column, column);

        using var dataTable = layout.CreateDataTable();
        foreach (var row in batch)
            layout.AddRow(dataTable, row);

        await bulkCopy.WriteToServerAsync(dataTable, cancellationToken).ConfigureAwait(false);
```

7. Delete the now-unused `BuildDataTable` method.

- [ ] **Step 5: Wire the layout into the sink**

Replace `src/Vestige.Sinks.SqlServer/SqlServerSink.cs` with:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Vestige.Sinks.SqlServer;

/// <summary>Persists wide events to SQL Server using inserts for single rows and bulk copy for batches.</summary>
public sealed class SqlServerSink : IWideEventSink
{
    private readonly SqlServerSinkOptions _options;
    private readonly ILogger _logger;
    private readonly ISqlServerWriter _writer;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private SqlServerTableLayout? _layout;
    private PromotionWarningLimiter? _warnings;
    private int _initialized;
    private int _disposed;

    public SqlServerSink(IOptions<SqlServerSinkOptions> options)
        : this(options, NullLogger<SqlServerSink>.Instance)
    {
    }

    public SqlServerSink(IOptions<SqlServerSinkOptions> options, ILogger<SqlServerSink> logger)
        : this(options, logger, new SqlServerWriter(), TimeProvider.System)
    {
    }

    internal SqlServerSink(
        IOptions<SqlServerSinkOptions> options,
        ILogger logger,
        ISqlServerWriter writer,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _options = options.Value;
        _logger = logger;
        _writer = writer;
        _timeProvider = timeProvider;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
            return;

        SqlServerSinkOptionsValidator.Validate(_options);
        var layout = new SqlServerTableLayout(_options);
        _warnings = new PromotionWarningLimiter(_logger, _timeProvider);

        await _writer.InitializeAsync(_options, layout, cancellationToken).ConfigureAwait(false);

        if (_options.AutoCreate)
            await _writer.EnsureStorageAsync(cancellationToken).ConfigureAwait(false);
        else
            await _writer.VerifyStorageAsync(cancellationToken).ConfigureAwait(false);

        _layout = layout;
    }

    public Task EmitAsync(WideEventData data, CancellationToken cancellationToken = default)
        => WriteAsync([MapRow(data)], cancellationToken);

    public async Task EmitBatchAsync(IReadOnlyList<WideEventData> batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.Count == 0)
            return;

        var rows = new List<SqlServerEventRow>(batch.Count);
        foreach (var data in batch)
            rows.Add(MapRow(data));

        await WriteAsync(rows, cancellationToken).ConfigureAwait(false);
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
        => WaitForInFlightWriteAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await WaitForInFlightWriteAsync(CancellationToken.None).ConfigureAwait(false);
        await _writer.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }

    private SqlServerEventRow MapRow(WideEventData data)
    {
        var layout = _layout ?? throw new InvalidOperationException("SQL Server sink must be initialized before emitting events.");
        return SqlServerEventRow.Map(data, layout.Promoted, _warnings);
    }

    private async Task WriteAsync(IReadOnlyList<SqlServerEventRow> rows, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (Volatile.Read(ref _initialized) == 0)
            throw new InvalidOperationException("SQL Server sink must be initialized before emitting events.");

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (rows.Count == 1)
                await _writer.WriteSingleAsync(rows[0], cancellationToken).ConfigureAwait(false);
            else
                await _writer.WriteAsync(rows, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WaitForInFlightWriteAsync(CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        _writeLock.Release();
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass, then run the whole unit suite**

Run: `dotnet test tests/Vestige.Tests -- --filter-class "Vestige.Tests.SqlServerTableLayoutTests" --filter-class "Vestige.Tests.SqlServerSinkTests"`
Expected: PASS (11 tests).

Run: `dotnet build Vestige.slnx -c Release` and then `dotnet test tests/Vestige.Tests`
Expected: the build has 0 warnings in `src/`, and all unit tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/Vestige.Sinks.SqlServer tests/Vestige.Tests
git commit -m "feat(sqlserver): drive inserts and bulk copy from a cached table layout"
```

---

### Task 6: Schema SQL builders, schema diff and index strategy

**Files:**
- Create: `src/Vestige.Sinks.SqlServer/SqlServerSchemaSql.cs`, `src/Vestige.Sinks.SqlServer/SchemaDiff.cs`, `src/Vestige.Sinks.SqlServer/IndexBuildStrategy.cs`
- Test: `tests/Vestige.Tests/SqlServerSchemaSqlTests.cs`, `tests/Vestige.Tests/SchemaDiffTests.cs`, `tests/Vestige.Tests/IndexBuildStrategyTests.cs`

**Interfaces:**
- Consumes: `SqlServerTableLayout` (Task 5); `PromotedField`, `PromotedType.Matches` (Task 3).
- Produces:
  - `internal static class SqlServerSchemaSql`:
    - DDL builders: `CreateSchema(SqlServerSinkOptions)`, `CreateTable(layout)`, `CreateFixedIndexes(layout)`, `AddColumn(layout, field)`, `CreateIndex(layout, field, bool online)`, `CreateIndexIfMissing(layout, field)`.
    - Query consts (all take `@object_name`): `TableExists`, `SelectColumns`, `SelectIndexNames`.
    - `EngineEdition` const (no parameters).
  - `internal sealed record ExistingColumn(string Name, string TypeName, short MaxLength, byte Scale, bool IsNullable)`
  - `internal sealed record SchemaDiff(IReadOnlyList<PromotedField> MissingColumns, IReadOnlyList<PromotedField> MissingIndexes)` with `static Compute(layout, IReadOnlyList<ExistingColumn>, IReadOnlySet<string>)` and `RequireNoMissingColumns()`
  - `internal enum IndexBuildMode { Online, Skip }` and `internal static IndexBuildStrategy.ForEngineEdition(int)`

- [ ] **Step 1: Write the failing tests**

`tests/Vestige.Tests/SqlServerSchemaSqlTests.cs`:

```csharp
using Vestige.Sinks.SqlServer;

namespace Vestige.Tests;

public sealed class SqlServerSchemaSqlTests
{
    private static SqlServerTableLayout Layout(Action<SqlServerSinkOptions>? configure = null)
    {
        var options = new SqlServerSinkOptions { ConnectionString = "Server=.;Database=x" };
        configure?.Invoke(options);
        return new SqlServerTableLayout(options);
    }

    [Fact]
    public void CreateTable_WithoutPromotedFields_HasExactly020Columns()
    {
        var sql = SqlServerSchemaSql.CreateTable(Layout());

        Assert.StartsWith("IF OBJECT_ID(N'[dbo].[wide_events]', N'U') IS NULL", sql);
        Assert.Contains("[id] BIGINT IDENTITY(1,1) PRIMARY KEY,", sql);
        Assert.Contains("[payload] NVARCHAR(MAX) NOT NULL", sql);
        Assert.Equal(16, sql.Split('\n').Count(line => line.TrimStart().StartsWith('[')));
    }

    [Fact]
    public void CreateTable_WithPromotedFields_AppendsNullableTypedColumns()
    {
        var sql = SqlServerSchemaSql.CreateTable(Layout(o =>
        {
            o.PromotedFields.Add("audit_action", PromotedType.String(128));
            o.PromotedFields.Add("tenant_id", PromotedType.Guid);
        }));

        Assert.Contains("[payload] NVARCHAR(MAX) NOT NULL,\n        [audit_action] NVARCHAR(128) NULL,\n        [tenant_id] UNIQUEIDENTIFIER NULL\n    );", sql);
    }

    [Fact]
    public void CreateIndex_Online_TargetsColumnThenTimestampDesc()
    {
        var layout = Layout(o => o.PromotedFields.Add("audit_action", PromotedType.String(128)));

        Assert.Equal(
            "CREATE INDEX [dbo_wide_events_audit_action_idx] ON [dbo].[wide_events] ([audit_action], [timestamp_utc] DESC) WITH (ONLINE = ON);",
            SqlServerSchemaSql.CreateIndex(layout, layout.Promoted[0], online: true));
        Assert.Equal(
            "CREATE INDEX [dbo_wide_events_audit_action_idx] ON [dbo].[wide_events] ([audit_action], [timestamp_utc] DESC);",
            SqlServerSchemaSql.CreateIndex(layout, layout.Promoted[0], online: false));
    }

    [Fact]
    public void CreateIndexIfMissing_GuardsOnIndexName()
    {
        var layout = Layout(o => o.PromotedFields.Add("audit_action", PromotedType.String(128)));

        var sql = SqlServerSchemaSql.CreateIndexIfMissing(layout, layout.Promoted[0]);

        Assert.StartsWith(
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'dbo_wide_events_audit_action_idx' AND object_id = OBJECT_ID(N'[dbo].[wide_events]'))",
            sql);
        Assert.EndsWith("([audit_action], [timestamp_utc] DESC);", sql);
    }

    [Fact]
    public void AddColumn_IsNullable()
    {
        var layout = Layout(o => o.PromotedFields.Add("tenant_id", PromotedType.Guid));

        Assert.Equal(
            "ALTER TABLE [dbo].[wide_events] ADD [tenant_id] UNIQUEIDENTIFIER NULL;",
            SqlServerSchemaSql.AddColumn(layout, layout.Promoted[0]));
    }

    [Fact]
    public void Ddl_EscapesQuotesAndBracketsInNames()
    {
        var layout = Layout(o =>
        {
            o.Table = "we]ird'name";
            o.PromotedFields.Add("audit_action", PromotedType.String(128));
        });

        var create = SqlServerSchemaSql.CreateTable(layout);
        var guard = SqlServerSchemaSql.CreateIndexIfMissing(layout, layout.Promoted[0]);

        Assert.StartsWith("IF OBJECT_ID(N'[dbo].[we]]ird''name]', N'U') IS NULL", create);
        Assert.Contains("CREATE TABLE [dbo].[we]]ird'name] (", create);
        Assert.Contains("name = N'dbo_we]ird''name_audit_action_idx'", guard);
        Assert.Contains("CREATE INDEX [dbo_we]]ird'name_audit_action_idx]", guard);
    }
}
```

`tests/Vestige.Tests/SchemaDiffTests.cs`:

```csharp
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
```

`tests/Vestige.Tests/IndexBuildStrategyTests.cs`:

```csharp
using Vestige.Sinks.SqlServer;

namespace Vestige.Tests;

public sealed class IndexBuildStrategyTests
{
    [Theory]
    [InlineData(3, IndexBuildMode.Online)]
    [InlineData(5, IndexBuildMode.Online)]
    [InlineData(8, IndexBuildMode.Online)]
    [InlineData(1, IndexBuildMode.Skip)]
    [InlineData(2, IndexBuildMode.Skip)]
    [InlineData(4, IndexBuildMode.Skip)]
    [InlineData(6, IndexBuildMode.Skip)]
    [InlineData(9, IndexBuildMode.Skip)]
    public void ForEngineEdition_OnlineOnlyWhereSupported(int edition, IndexBuildMode expected)
    {
        Assert.Equal(expected, IndexBuildStrategy.ForEngineEdition(edition));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Vestige.Tests -- --filter-class "Vestige.Tests.SqlServerSchemaSqlTests" --filter-class "Vestige.Tests.SchemaDiffTests" --filter-class "Vestige.Tests.IndexBuildStrategyTests"`
Expected: the build FAILS with `CS0103: The name 'SqlServerSchemaSql' does not exist in the current context`.

- [ ] **Step 3: Implement**

`src/Vestige.Sinks.SqlServer/SqlServerSchemaSql.cs`:

```csharp
using System.Text;

namespace Vestige.Sinks.SqlServer;

/// <summary>Pure DDL and catalog-query text. Nothing here touches a connection.</summary>
internal static class SqlServerSchemaSql
{
    public const string TableExists =
        "SELECT CASE WHEN OBJECT_ID(@object_name, N'U') IS NULL THEN 0 ELSE 1 END";

    public const string SelectColumns = """
        SELECT c.name, t.name, c.max_length, c.scale, c.is_nullable
        FROM sys.columns c
        JOIN sys.types t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(@object_name, N'U')
        """;

    public const string SelectIndexNames =
        "SELECT name FROM sys.indexes WHERE object_id = OBJECT_ID(@object_name, N'U') AND name IS NOT NULL";

    public const string EngineEdition = "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int)";

    public static string CreateSchema(SqlServerSinkOptions options)
    {
        var schemaLiteral = SqlServerTableLayout.Literal(options.Schema);
        var schemaForExec = SqlServerTableLayout.Literal(SqlServerTableLayout.QuoteIdentifier(options.Schema));
        return $"IF SCHEMA_ID(N'{schemaLiteral}') IS NULL EXEC('CREATE SCHEMA {schemaForExec}')";
    }

    public static string CreateTable(SqlServerTableLayout layout)
    {
        var sql = new StringBuilder();
        sql.Append($"IF OBJECT_ID(N'{layout.QualifiedTableLiteral}', N'U') IS NULL\n");
        sql.Append($"    CREATE TABLE {layout.QualifiedTable} (\n");
        sql.Append("        [id] BIGINT IDENTITY(1,1) PRIMARY KEY,\n");
        sql.Append("        [event_id] VARCHAR(256) NOT NULL,\n");
        sql.Append("        [timestamp_utc] DATETIMEOFFSET(7) NOT NULL,\n");
        sql.Append("        [service_name] NVARCHAR(256) NULL,\n");
        sql.Append("        [service_version] NVARCHAR(64) NULL,\n");
        sql.Append("        [deployment_environment] NVARCHAR(128) NULL,\n");
        sql.Append("        [cloud_region] NVARCHAR(128) NULL,\n");
        sql.Append("        [trace_id] NVARCHAR(64) NULL,\n");
        sql.Append("        [span_id] NVARCHAR(32) NULL,\n");
        sql.Append("        [outcome] NVARCHAR(32) NULL,\n");
        sql.Append("        [duration_ms] FLOAT NOT NULL,\n");
        sql.Append("        [http_status_code] INT NULL,\n");
        sql.Append("        [http_method] NVARCHAR(16) NULL,\n");
        sql.Append("        [http_path] NVARCHAR(2048) NULL,\n");
        sql.Append("        [error_type] NVARCHAR(512) NULL,\n");
        sql.Append("        [payload] NVARCHAR(MAX) NOT NULL");
        foreach (var field in layout.Promoted)
            sql.Append($",\n        {SqlServerTableLayout.QuoteIdentifier(field.Column)} {field.Type.SqlTypeName} NULL");
        sql.Append("\n    );");
        return sql.ToString();
    }

    public static string CreateFixedIndexes(SqlServerTableLayout layout)
    {
        var sql = new StringBuilder();
        foreach (var (suffix, column) in new[]
                 {
                     ("event_id_idx", "event_id"),
                     ("timestamp_idx", "timestamp_utc"),
                     ("service_name_idx", "service_name"),
                     ("outcome_idx", "outcome"),
                     ("trace_id_idx", "trace_id"),
                 })
        {
            var name = $"{layout.IndexPrefix}_{suffix}";
            sql.Append(IndexGuard(layout, name));
            sql.Append($"    CREATE INDEX {SqlServerTableLayout.QuoteIdentifier(name)} ON {layout.QualifiedTable} ([{column}]);\n");
        }

        return sql.ToString();
    }

    public static string AddColumn(SqlServerTableLayout layout, PromotedField field)
        => $"ALTER TABLE {layout.QualifiedTable} ADD {SqlServerTableLayout.QuoteIdentifier(field.Column)} {field.Type.SqlTypeName} NULL;";

    public static string CreateIndex(SqlServerTableLayout layout, PromotedField field, bool online)
        => $"CREATE INDEX {SqlServerTableLayout.QuoteIdentifier(layout.IndexName(field))} ON {layout.QualifiedTable} " +
           $"({SqlServerTableLayout.QuoteIdentifier(field.Column)}, [timestamp_utc] DESC)" +
           (online ? " WITH (ONLINE = ON);" : ";");

    public static string CreateIndexIfMissing(SqlServerTableLayout layout, PromotedField field)
        => IndexGuard(layout, layout.IndexName(field)) + "    " + CreateIndex(layout, field, online: false);

    private static string IndexGuard(SqlServerTableLayout layout, string indexName)
        => $"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{SqlServerTableLayout.Literal(indexName)}' " +
           $"AND object_id = OBJECT_ID(N'{layout.QualifiedTableLiteral}'))\n";
}
```

`src/Vestige.Sinks.SqlServer/SchemaDiff.cs`:

```csharp
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
```

`src/Vestige.Sinks.SqlServer/IndexBuildStrategy.cs`:

```csharp
namespace Vestige.Sinks.SqlServer;

internal enum IndexBuildMode
{
    Online,
    Skip,
}

internal static class IndexBuildStrategy
{
    /// <summary>
    /// <c>SERVERPROPERTY('EngineEdition')</c>: 3 Enterprise/Developer, 5 Azure SQL Database,
    /// 8 Managed Instance support <c>ONLINE = ON</c>; anything else would block inserts.
    /// </summary>
    public static IndexBuildMode ForEngineEdition(int engineEdition)
        => engineEdition is 3 or 5 or 8 ? IndexBuildMode.Online : IndexBuildMode.Skip;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Vestige.Tests -- --filter-class "Vestige.Tests.SqlServerSchemaSqlTests" --filter-class "Vestige.Tests.SchemaDiffTests" --filter-class "Vestige.Tests.IndexBuildStrategyTests"`
Expected: PASS (21 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Vestige.Sinks.SqlServer tests/Vestige.Tests
git commit -m "feat(sqlserver): add pure schema DDL builders, schema diff and index strategy"
```

---

### Task 7: Storage creation and upgrade, with integration tests

**Files:**
- Create: `tests/Vestige.Sinks.IntegrationTests/Vestige.Sinks.IntegrationTests.csproj`, `tests/Vestige.Sinks.IntegrationTests/AssemblyFixtures.cs`, `tests/Vestige.Sinks.IntegrationTests/Support/SqlServerFixture.cs`, `tests/Vestige.Sinks.IntegrationTests/Support/SqlTestDb.cs`, `tests/Vestige.Sinks.IntegrationTests/Support/Legacy020Schema.cs`, `tests/Vestige.Sinks.IntegrationTests/Support/TestEvents.cs`, `tests/Vestige.Sinks.IntegrationTests/Support/ListLogger.cs`, `tests/Vestige.Sinks.IntegrationTests/SqlServerStorageTests.cs`
- Modify: `Directory.Packages.props`, `Vestige.slnx`, `src/Vestige.Sinks.SqlServer/SqlServerWriter.cs`

**Interfaces:**
- Consumes:
  - From Task 6: `SqlServerSchemaSql.*`, `SchemaDiff`, `ExistingColumn`.
  - From Task 5: `SqlServerTableLayout`, `SqlServerSink` constructors.
- Produces:
  - Real `SqlServerWriter.EnsureStorageAsync` (returns indexes still to build on existing tables) and `VerifyStorageAsync`.
  - Integration support used by Tasks 8–10:
    - `SqlServerFixture { string ConnectionString; void SkipIfUnavailable(); }`
    - `SqlTestDb.UniqueTable()`, `ScalarAsync<T>(cs, sql)`, `ExecuteAsync(cs, sql)`, `ColumnsAsync(cs, schema, table)`, `IndexNamesAsync(cs, schema, table)`
    - `Legacy020Schema.CreateAsync(cs, schema, table)`
    - `TestEvents.Make(Action<WideEvent>?)`
    - `ListLogger<T>`
    - `SinkFactory.SqlServer(cs, table, configure, logger)` (defined in `SqlTestDb.cs`)

- [ ] **Step 1: Create the integration test project**

In `Directory.Packages.props`, inside `<ItemGroup Label="Test">`, add:

```xml
    <PackageVersion Include="Testcontainers.MsSql" Version="4.4.0" />
    <PackageVersion Include="Testcontainers.PostgreSql" Version="4.4.0" />
```

`tests/Vestige.Sinks.IntegrationTests/Vestige.Sinks.IntegrationTests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <GenerateProgramFile>false</GenerateProgramFile>
    <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="xunit.v3.runner.console">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="Testcontainers.MsSql" />
    <PackageReference Include="Testcontainers.PostgreSql" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Vestige\Vestige.csproj" />
    <ProjectReference Include="..\..\src\Vestige.Sinks.SqlServer\Vestige.Sinks.SqlServer.csproj" />
    <ProjectReference Include="..\..\src\Vestige.Sinks.Postgres\Vestige.Sinks.Postgres.csproj" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

</Project>
```

In `Vestige.slnx`, inside `<Folder Name="/tests/">`, add:

```xml
    <Project Path="tests/Vestige.Sinks.IntegrationTests/Vestige.Sinks.IntegrationTests.csproj" />
```

- [ ] **Step 2: Write the integration support**

`tests/Vestige.Sinks.IntegrationTests/AssemblyFixtures.cs`:

```csharp
using Vestige.Sinks.IntegrationTests.Support;

[assembly: AssemblyFixture(typeof(SqlServerFixture))]
```

`tests/Vestige.Sinks.IntegrationTests/Support/SqlServerFixture.cs`:

```csharp
using Testcontainers.MsSql;

namespace Vestige.Sinks.IntegrationTests.Support;

/// <summary>
/// One SQL Server 2022 Developer container (EngineEdition 3) per test run. Tests are skipped when
/// Docker is unavailable, unless VESTIGE_REQUIRE_DOCKER=1 (CI), in which case startup fails.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private MsSqlContainer? _container;
    private string? _unavailableReason;

    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new MsSqlBuilder().WithImage("mcr.microsoft.com/mssql/server:2022-latest").Build();
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }
        catch (Exception ex) when (Environment.GetEnvironmentVariable("VESTIGE_REQUIRE_DOCKER") != "1")
        {
            _unavailableReason = $"SQL Server container unavailable: {ex.Message}";
        }
    }

    public void SkipIfUnavailable()
    {
        if (_unavailableReason is not null)
            Assert.Skip(_unavailableReason);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
```

`tests/Vestige.Sinks.IntegrationTests/Support/ListLogger.cs`:

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Vestige.Sinks.IntegrationTests.Support;

public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

public sealed class ListLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
}
```

`tests/Vestige.Sinks.IntegrationTests/Support/TestEvents.cs`:

```csharp
using Vestige.Internal;

namespace Vestige.Sinks.IntegrationTests.Support;

public static class TestEvents
{
    public static WideEventData Make(Action<WideEvent>? configure = null)
    {
        var ev = new WideEvent { ServiceName = "integration", DurationMs = 2, Outcome = "success", StatusCode = 200 };
        ev.Set("http.method", "POST");
        ev.Set("http.path", "/tenants");
        configure?.Invoke(ev);
        return new SystemTextJsonSerializer().Serialize(ev);
    }
}
```

`tests/Vestige.Sinks.IntegrationTests/Support/SqlTestDb.cs`:

```csharp
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Vestige.Sinks.SqlServer;

namespace Vestige.Sinks.IntegrationTests.Support;

public static class SqlTestDb
{
    public static string UniqueTable() => "we_" + Guid.NewGuid().ToString("N")[..12];

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return (T)value!;
    }

    public static async Task<Dictionary<string, string>> ColumnsAsync(string connectionString, string schema, string table)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT c.name, t.name + CASE WHEN c.is_nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END " +
            "FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id " +
            "WHERE c.object_id = OBJECT_ID(@name, N'U')",
            connection);
        command.Parameters.AddWithValue("@name", $"[{schema}].[{table}]");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    public static async Task<HashSet<string>> IndexNamesAsync(string connectionString, string schema, string table)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT name FROM sys.indexes WHERE object_id = OBJECT_ID(@name, N'U') AND name IS NOT NULL", connection);
        command.Parameters.AddWithValue("@name", $"[{schema}].[{table}]");
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(reader.GetString(0));
        return result;
    }
}

public static class SinkFactory
{
    public static SqlServerSink SqlServer(
        string connectionString,
        string table,
        Action<SqlServerSinkOptions>? configure = null,
        ListLogger<SqlServerSink>? logger = null)
    {
        var options = new SqlServerSinkOptions { ConnectionString = connectionString, Schema = "dbo", Table = table };
        configure?.Invoke(options);
        return new SqlServerSink(Options.Create(options), logger ?? new ListLogger<SqlServerSink>());
    }
}
```

`tests/Vestige.Sinks.IntegrationTests/Support/Legacy020Schema.cs`:

```csharp
namespace Vestige.Sinks.IntegrationTests.Support;

/// <summary>The literal 0.2.0 DDL, copied here so upgrade tests start from what production really has.</summary>
public static class Legacy020Schema
{
    public static Task CreateAsync(string connectionString, string schema, string table)
    {
        var prefix = $"{schema}_{table}";
        return SqlTestDb.ExecuteAsync(connectionString, $"""
            CREATE TABLE [{schema}].[{table}] (
                [id] BIGINT IDENTITY(1,1) PRIMARY KEY,
                [event_id] VARCHAR(256) NOT NULL,
                [timestamp_utc] DATETIMEOFFSET(7) NOT NULL,
                [service_name] NVARCHAR(256) NULL,
                [service_version] NVARCHAR(64) NULL,
                [deployment_environment] NVARCHAR(128) NULL,
                [cloud_region] NVARCHAR(128) NULL,
                [trace_id] NVARCHAR(64) NULL,
                [span_id] NVARCHAR(32) NULL,
                [outcome] NVARCHAR(32) NULL,
                [duration_ms] FLOAT NOT NULL,
                [http_status_code] INT NULL,
                [http_method] NVARCHAR(16) NULL,
                [http_path] NVARCHAR(2048) NULL,
                [error_type] NVARCHAR(512) NULL,
                [payload] NVARCHAR(MAX) NOT NULL
            );
            CREATE INDEX [{prefix}_event_id_idx] ON [{schema}].[{table}] ([event_id]);
            CREATE INDEX [{prefix}_timestamp_idx] ON [{schema}].[{table}] ([timestamp_utc]);
            CREATE INDEX [{prefix}_service_name_idx] ON [{schema}].[{table}] ([service_name]);
            CREATE INDEX [{prefix}_outcome_idx] ON [{schema}].[{table}] ([outcome]);
            CREATE INDEX [{prefix}_trace_id_idx] ON [{schema}].[{table}] ([trace_id]);
            INSERT INTO [{schema}].[{table}] (event_id, timestamp_utc, duration_ms, payload)
            VALUES ('legacy-1', SYSDATETIMEOFFSET(), 1, N'{{"audit_action":"tenant.create"}}');
            """);
    }
}
```

- [ ] **Step 3: Write the failing integration tests**

`tests/Vestige.Sinks.IntegrationTests/SqlServerStorageTests.cs`:

```csharp
using Vestige.Sinks.IntegrationTests.Support;
using Vestige.Sinks.SqlServer;

namespace Vestige.Sinks.IntegrationTests;

public sealed class SqlServerStorageTests(SqlServerFixture db)
{
    private static readonly Guid Tenant = Guid.Parse("6f1c2a5e-1d2b-4c3d-9e8f-0a1b2c3d4e5f");

    private static void Promote(SqlServerSinkOptions o)
    {
        o.PromotedFields.Add("audit_action", PromotedType.String(128));
        o.PromotedFields.Add("tenant_id", PromotedType.Guid);
    }

    [Fact]
    public async Task NoPromotedFields_CreatesSameColumnsAs020()
    {
        db.SkipIfUnavailable();
        var legacy = SqlTestDb.UniqueTable();
        var current = SqlTestDb.UniqueTable();
        await Legacy020Schema.CreateAsync(db.ConnectionString, "dbo", legacy);
        await using var sink = SinkFactory.SqlServer(db.ConnectionString, current);

        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            await SqlTestDb.ColumnsAsync(db.ConnectionString, "dbo", legacy),
            await SqlTestDb.ColumnsAsync(db.ConnectionString, "dbo", current));
    }

    [Fact]
    public async Task NewTable_CreatesPromotedColumnsAndIndexesInline()
    {
        db.SkipIfUnavailable();
        var table = SqlTestDb.UniqueTable();
        await using var sink = SinkFactory.SqlServer(db.ConnectionString, table, Promote);

        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        var columns = await SqlTestDb.ColumnsAsync(db.ConnectionString, "dbo", table);
        Assert.Equal("nvarchar NULL", columns["audit_action"]);
        Assert.Equal("uniqueidentifier NULL", columns["tenant_id"]);
        var indexes = await SqlTestDb.IndexNamesAsync(db.ConnectionString, "dbo", table);
        Assert.Contains($"dbo_{table}_audit_action_idx", indexes);
        Assert.Contains($"dbo_{table}_tenant_id_idx", indexes);
    }

    [Fact]
    public async Task SingleInsertAndBulkCopy_PersistPromotedValues()
    {
        db.SkipIfUnavailable();
        var table = SqlTestDb.UniqueTable();
        await using var sink = SinkFactory.SqlServer(db.ConnectionString, table, Promote);
        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        await sink.EmitAsync(TestEvents.Make(ev =>
        {
            ev.Set("audit_action", "tenant.create");
            ev.Set("tenant_id", Tenant);
        }), TestContext.Current.CancellationToken);
        await sink.EmitBatchAsync(
        [
            TestEvents.Make(ev => ev.Set("audit_action", "api_key.revoke")),
            TestEvents.Make(ev => ev.Set("tenant_id", Tenant.ToString())),
        ], TestContext.Current.CancellationToken);

        Assert.Equal(1, await SqlTestDb.ScalarAsync<int>(db.ConnectionString,
            $"SELECT COUNT(*) FROM dbo.[{table}] WHERE audit_action = N'tenant.create' AND tenant_id = '{Tenant}'"));
        Assert.Equal(1, await SqlTestDb.ScalarAsync<int>(db.ConnectionString,
            $"SELECT COUNT(*) FROM dbo.[{table}] WHERE audit_action = N'api_key.revoke' AND tenant_id IS NULL"));
        Assert.Equal(1, await SqlTestDb.ScalarAsync<int>(db.ConnectionString,
            $"SELECT COUNT(*) FROM dbo.[{table}] WHERE audit_action IS NULL AND tenant_id = '{Tenant}'"));
    }

    [Fact]
    public async Task ExistingColumnOfDifferentType_InitializeThrows()
    {
        db.SkipIfUnavailable();
        var table = SqlTestDb.UniqueTable();
        await Legacy020Schema.CreateAsync(db.ConnectionString, "dbo", table);
        await SqlTestDb.ExecuteAsync(db.ConnectionString, $"ALTER TABLE dbo.[{table}] ADD tenant_id INT NULL");
        await using var sink = SinkFactory.SqlServer(db.ConnectionString, table, Promote);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.InitializeAsync(TestContext.Current.CancellationToken));
        Assert.Contains("'tenant_id'", ex.Message);
    }

    [Fact]
    public async Task ExistingNotNullColumn_InitializeThrows()
    {
        db.SkipIfUnavailable();
        var table = SqlTestDb.UniqueTable();
        await Legacy020Schema.CreateAsync(db.ConnectionString, "dbo", table);
        await SqlTestDb.ExecuteAsync(db.ConnectionString,
            $"ALTER TABLE dbo.[{table}] ADD tenant_id UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID()");
        await using var sink = SinkFactory.SqlServer(db.ConnectionString, table, Promote);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.InitializeAsync(TestContext.Current.CancellationToken));
        Assert.Contains("NOT NULL", ex.Message);
    }

    [Fact]
    public async Task AutoCreateDisabled_MissingPromotedColumn_InitializeThrows()
    {
        db.SkipIfUnavailable();
        var table = SqlTestDb.UniqueTable();
        await Legacy020Schema.CreateAsync(db.ConnectionString, "dbo", table);
        await using var sink = SinkFactory.SqlServer(db.ConnectionString, table, o =>
        {
            Promote(o);
            o.AutoCreate = false;
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.InitializeAsync(TestContext.Current.CancellationToken));
        Assert.Contains("AutoCreate is disabled", ex.Message);
        Assert.DoesNotContain("audit_action", (await SqlTestDb.ColumnsAsync(db.ConnectionString, "dbo", table)).Keys);
    }

    [Fact]
    public async Task ExistingTable_AddsMissingColumnsAsNullable()
    {
        db.SkipIfUnavailable();
        var table = SqlTestDb.UniqueTable();
        await Legacy020Schema.CreateAsync(db.ConnectionString, "dbo", table);
        await using var sink = SinkFactory.SqlServer(db.ConnectionString, table, Promote);

        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        var columns = await SqlTestDb.ColumnsAsync(db.ConnectionString, "dbo", table);
        Assert.Equal("nvarchar NULL", columns["audit_action"]);
        Assert.Equal("uniqueidentifier NULL", columns["tenant_id"]);
        Assert.Equal(1, await SqlTestDb.ScalarAsync<int>(db.ConnectionString,
            $"SELECT COUNT(*) FROM dbo.[{table}] WHERE event_id = 'legacy-1' AND audit_action IS NULL"));
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test tests/Vestige.Sinks.IntegrationTests -- --filter-class "Vestige.Sinks.IntegrationTests.SqlServerStorageTests"`
Expected: FAIL. The new-table tests fail because the promoted columns aren't created (`KeyNotFoundException: 'audit_action'`). `ExistingTable_AddsMissingColumnsAsNullable` fails the same way. The type-mismatch, NOT NULL and AutoCreate-disabled tests fail with `Assert.ThrowsAsync() Failure: No exception was thrown`. `NoPromotedFields_CreatesSameColumnsAs020` passes. If Docker is missing, every test reports Skipped; fix Docker before continuing.

- [ ] **Step 5: Implement storage creation and upgrade**

In `src/Vestige.Sinks.SqlServer/SqlServerWriter.cs`, replace the whole `EnsureStorageAsync` and `VerifyStorageAsync` methods with the code below, and add the private helpers after them:

```csharp
    public async Task<IReadOnlyList<PromotedField>> EnsureStorageAsync(CancellationToken cancellationToken)
    {
        var connection = await GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var options = _options ?? throw new InvalidOperationException("Sink options have not been initialized.");
        var layout = _layout ?? throw new InvalidOperationException("Sink layout has not been initialized.");

        await ExecuteAsync(connection, SqlServerSchemaSql.CreateSchema(options), cancellationToken).ConfigureAwait(false);

        var tableExisted = await TableExistsAsync(connection, layout, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, SqlServerSchemaSql.CreateTable(layout), cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, SqlServerSchemaSql.CreateFixedIndexes(layout), cancellationToken).ConfigureAwait(false);

        if (!tableExisted)
        {
            // Empty table: building inline is instant. The guard covers a replica that won the CREATE TABLE race.
            foreach (var field in layout.Promoted.Where(f => f.Indexed))
                await ExecuteAsync(connection, SqlServerSchemaSql.CreateIndexIfMissing(layout, field), cancellationToken).ConfigureAwait(false);
            return [];
        }

        var diff = await ReadDiffAsync(connection, layout, cancellationToken).ConfigureAwait(false);
        foreach (var field in diff.MissingColumns)
            await ExecuteAsync(connection, SqlServerSchemaSql.AddColumn(layout, field), cancellationToken).ConfigureAwait(false);

        return diff.MissingIndexes;
    }

    public async Task VerifyStorageAsync(CancellationToken cancellationToken)
    {
        var layout = _layout ?? throw new InvalidOperationException("Sink layout has not been initialized.");
        if (layout.Promoted.Count == 0)
            return;

        var connection = await GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var diff = await ReadDiffAsync(connection, layout, cancellationToken).ConfigureAwait(false);
        diff.RequireNoMissingColumns();
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> TableExistsAsync(SqlConnection connection, SqlServerTableLayout layout, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(SqlServerSchemaSql.TableExists, connection);
        command.Parameters.Add(new SqlParameter("@object_name", SqlDbType.NVarChar, 512) { Value = layout.QualifiedTable });
        return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 1;
    }

    private static async Task<SchemaDiff> ReadDiffAsync(SqlConnection connection, SqlServerTableLayout layout, CancellationToken cancellationToken)
    {
        var columns = new List<ExistingColumn>();
        await using (var command = new SqlCommand(SqlServerSchemaSql.SelectColumns, connection))
        {
            command.Parameters.Add(new SqlParameter("@object_name", SqlDbType.NVarChar, 512) { Value = layout.QualifiedTable });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                columns.Add(new ExistingColumn(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt16(2),
                    reader.GetByte(3),
                    reader.GetBoolean(4)));
            }
        }

        var indexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = new SqlCommand(SqlServerSchemaSql.SelectIndexNames, connection))
        {
            command.Parameters.Add(new SqlParameter("@object_name", SqlDbType.NVarChar, 512) { Value = layout.QualifiedTable });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                indexes.Add(reader.GetString(0));
        }

        return SchemaDiff.Compute(layout, columns, indexes);
    }
```

The old inline DDL strings and the `schema`/`table`/`schemaForSchemaId`/`schemaForExec`/`indexPrefix` locals are removed along with the old body.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Vestige.Sinks.IntegrationTests -- --filter-class "Vestige.Sinks.IntegrationTests.SqlServerStorageTests"`
Expected: PASS (7 tests). The first run pulls the image, which takes a few minutes.

Run: `dotnet test tests/Vestige.Tests`
Expected: all unit tests pass.

- [ ] **Step 7: Commit**

```bash
git add Directory.Packages.props Vestige.slnx src/Vestige.Sinks.SqlServer/SqlServerWriter.cs tests/Vestige.Sinks.IntegrationTests
git commit -m "feat(sqlserver): create and upgrade promoted columns, verify when AutoCreate is off"
```

---

### Task 8: Background online index builds and sink lifecycle

**Files:**
- Create: `src/Vestige.Sinks.SqlServer/SqlAppLock.cs`, `src/Vestige.Sinks.SqlServer/SqlServerIndexBuilder.cs`, `tests/Vestige.Sinks.IntegrationTests/SqlServerIndexBuildTests.cs`
- Modify: `src/Vestige.Sinks.SqlServer/SqlServerSink.cs`
- Test: `tests/Vestige.Tests/SqlServerSinkTests.cs` (add tests), `tests/Vestige.Sinks.IntegrationTests/SqlServerIndexBuildTests.cs`

**Interfaces:**
- Consumes:
  - From Task 6: `SqlServerSchemaSql.CreateIndex(layout, field, online: true)`, `SqlServerSchemaSql.EngineEdition`, `SqlServerSchemaSql.SelectIndexNames`, `IndexBuildStrategy`.
  - From Task 5: `SqlServerTableLayout.IndexName`, and `FakeSqlServerWriter.PendingIndexes`.
- Produces:
  - `internal static class SqlAppLock { Task<bool> TryAcquireAsync(SqlConnection, string resource, CancellationToken); Task ReleaseAsync(SqlConnection, string resource); }`
  - `internal sealed class SqlServerIndexBuilder(string connectionString, SqlServerTableLayout layout, ILogger logger) { Task BuildAsync(IReadOnlyList<PromotedField> pending, CancellationToken); }`
  - `SqlServerSink`:
    - `internal Task IndexBuildCompletion` (completed task when nothing is pending)
    - internal ctor gains an optional last parameter `Func<SqlServerTableLayout, IReadOnlyList<PromotedField>, CancellationToken, Task>? buildIndexes = null`
    - `DisposeAsync` cancels and awaits background work.

- [ ] **Step 1: Write the failing unit tests**

Append these tests to `tests/Vestige.Tests/SqlServerSinkTests.cs` inside the class:

```csharp
    [Fact]
    public async Task Initialize_ReturnsBeforePendingIndexBuildFinishes()
    {
        var writer = new FakeSqlServerWriter();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<PromotedField>? built = null;
        var options = new SqlServerSinkOptions { ConnectionString = "Server=.;Database=x" };
        options.PromotedFields.Add("audit_action", PromotedType.String(128));
        writer.PendingIndexes = [options.PromotedFields[0]];
        await using var sink = new SqlServerSink(
            Options.Create(options), new ListLogger<SqlServerSink>(), writer, TimeProvider.System,
            buildIndexes: async (_, pending, ct) =>
            {
                built = pending;
                started.SetResult();
                await release.Task.WaitAsync(ct);
            });

        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(sink.IndexBuildCompletion.IsCompleted);
        Assert.Equal("audit_action", Assert.Single(built!).Column);
        release.SetResult();
        await sink.IndexBuildCompletion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Initialize_NothingPending_DoesNotStartBuilder()
    {
        var calls = 0;
        await using var sink = new SqlServerSink(
            Options.Create(new SqlServerSinkOptions { ConnectionString = "Server=.;Database=x" }),
            new ListLogger<SqlServerSink>(), new FakeSqlServerWriter(), TimeProvider.System,
            buildIndexes: (_, _, _) =>
            {
                calls++;
                return Task.CompletedTask;
            });

        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(sink.IndexBuildCompletion.IsCompletedSuccessfully);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Dispose_CancelsRunningIndexBuild_WithoutThrowing()
    {
        var writer = new FakeSqlServerWriter();
        var options = new SqlServerSinkOptions { ConnectionString = "Server=.;Database=x" };
        options.PromotedFields.Add("audit_action", PromotedType.String(128));
        writer.PendingIndexes = [options.PromotedFields[0]];
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new SqlServerSink(
            Options.Create(options), new ListLogger<SqlServerSink>(), writer, TimeProvider.System,
            buildIndexes: async (_, _, ct) =>
            {
                ct.Register(() => cancelled.TrySetResult());
                await Task.Delay(Timeout.Infinite, ct);
            });
        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        await sink.DisposeAsync();

        Assert.True(cancelled.Task.IsCompleted);
        Assert.True(writer.Disposed);
    }
```

- [ ] **Step 2: Write the failing integration tests**

`tests/Vestige.Sinks.IntegrationTests/SqlServerIndexBuildTests.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Vestige.Sinks.IntegrationTests.Support;
using Vestige.Sinks.SqlServer;

namespace Vestige.Sinks.IntegrationTests;

public sealed class SqlServerIndexBuildTests(SqlServerFixture db)
{
    private static void Promote(SqlServerSinkOptions o)
    {
        o.PromotedFields.Add("audit_action", PromotedType.String(128));
        o.PromotedFields.Add("tenant_id", PromotedType.Guid);
    }

    [Fact]
    public async Task UpgradeFrom020_AddsColumns_ThenBuildsIndexesOnlineInBackground()
    {
        db.SkipIfUnavailable();
        var table = SqlTestDb.UniqueTable();
        await Legacy020Schema.CreateAsync(db.ConnectionString, "dbo", table);
        var logger = new ListLogger<SqlServerSink>();
        await using var sink = SinkFactory.SqlServer(db.ConnectionString, table, Promote, logger);

        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        await sink.EmitAsync(TestEvents.Make(ev => ev.Set("audit_action", "tenant.create")), TestContext.Current.CancellationToken);
        await sink.IndexBuildCompletion.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        var indexes = await SqlTestDb.IndexNamesAsync(db.ConnectionString, "dbo", table);
        Assert.Contains($"dbo_{table}_audit_action_idx", indexes);
        Assert.Contains($"dbo_{table}_tenant_id_idx", indexes);
        Assert.Equal(1, await SqlTestDb.ScalarAsync<int>(db.ConnectionString,
            $"SELECT COUNT(*) FROM dbo.[{table}] WHERE event_id = 'legacy-1' AND audit_action IS NULL"));
        Assert.Equal(1, await SqlTestDb.ScalarAsync<int>(db.ConnectionString,
            $"SELECT COUNT(*) FROM dbo.[{table}] WHERE audit_action = N'tenant.create'"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("online"));
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task TwoReplicasUpgradingTogether_BothFinishWithoutErrors()
    {
        db.SkipIfUnavailable();
        var table = SqlTestDb.UniqueTable();
        await Legacy020Schema.CreateAsync(db.ConnectionString, "dbo", table);
        var loggerA = new ListLogger<SqlServerSink>();
        var loggerB = new ListLogger<SqlServerSink>();
        await using var a = SinkFactory.SqlServer(db.ConnectionString, table, Promote, loggerA);
        await using var b = SinkFactory.SqlServer(db.ConnectionString, table, Promote, loggerB);

        await Task.WhenAll(
            a.InitializeAsync(TestContext.Current.CancellationToken),
            b.InitializeAsync(TestContext.Current.CancellationToken));
        await Task.WhenAll(a.IndexBuildCompletion, b.IndexBuildCompletion)
            .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        var indexes = await SqlTestDb.IndexNamesAsync(db.ConnectionString, "dbo", table);
        Assert.Contains($"dbo_{table}_audit_action_idx", indexes);
        Assert.Contains($"dbo_{table}_tenant_id_idx", indexes);
        Assert.DoesNotContain(loggerA.Entries.Concat(loggerB.Entries), e => e.Level >= LogLevel.Error);
    }
}
```

`TwoReplicasUpgradingTogether` has both sinks call `ALTER TABLE ADD` concurrently. The loser can hit "Column names in each table must be unique". To handle that, Step 4 makes the writer re-read the diff and retry once when an add-column fails with SQL error 2705.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Vestige.Tests -- --filter-class "Vestige.Tests.SqlServerSinkTests"`
Expected: the build FAILS with `CS1739: The best overload for 'SqlServerSink' does not have a parameter named 'buildIndexes'`.

- [ ] **Step 4: Implement**

`src/Vestige.Sinks.SqlServer/SqlAppLock.cs`:

```csharp
using System.Data;
using Microsoft.Data.SqlClient;

namespace Vestige.Sinks.SqlServer;

/// <summary>Session-owned application locks: released explicitly, or automatically when the connection closes.</summary>
internal static class SqlAppLock
{
    public static async Task<bool> TryAcquireAsync(SqlConnection connection, string resource, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "DECLARE @result int; " +
            "EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; " +
            "SELECT @result;",
            connection);
        command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = resource });
        var result = (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return result >= 0;
    }

    public static async Task ReleaseAsync(SqlConnection connection, string resource)
    {
        await using var command = new SqlCommand(
            "EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';", connection);
        command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = resource });
        await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
```

`src/Vestige.Sinks.SqlServer/SqlServerIndexBuilder.cs`:

```csharp
using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Vestige.Sinks.SqlServer;

/// <summary>Builds promoted indexes on an existing table without blocking startup or inserts.</summary>
internal sealed class SqlServerIndexBuilder(string connectionString, SqlServerTableLayout layout, ILogger logger)
{
    public async Task BuildAsync(IReadOnlyList<PromotedField> pending, CancellationToken cancellationToken)
    {
        if (pending.Count == 0)
            return;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var edition = await GetEngineEditionAsync(connection, cancellationToken).ConfigureAwait(false);
        var mode = IndexBuildStrategy.ForEngineEdition(edition);

        foreach (var field in pending)
        {
            var name = layout.IndexName(field);
            var ddl = SqlServerSchemaSql.CreateIndex(layout, field, online: true);

            if (mode == IndexBuildMode.Skip)
            {
                logger.LogWarning(
                    "Vestige did not build index {Index}: engine edition {Edition} cannot build it online, and an offline index build would block inserts; run it during a maintenance window: {Ddl}",
                    name, edition, ddl.Replace(" WITH (ONLINE = ON)", string.Empty, StringComparison.Ordinal));
                continue;
            }

            try
            {
                await BuildOneAsync(connection, field, name, ddl, edition, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Vestige failed to build index {Index}.", name);
            }
        }
    }

    private async Task BuildOneAsync(
        SqlConnection connection,
        PromotedField field,
        string name,
        string ddl,
        int edition,
        CancellationToken cancellationToken)
    {
        if (!await SqlAppLock.TryAcquireAsync(connection, name, cancellationToken).ConfigureAwait(false))
        {
            logger.LogDebug("Vestige skipped index {Index}: another instance is building it.", name);
            return;
        }

        try
        {
            if (await IndexExistsAsync(connection, name, cancellationToken).ConfigureAwait(false))
                return;

            logger.LogInformation("Vestige building index {Index} online (engine edition {Edition}).", name, edition);
            var stopwatch = Stopwatch.StartNew();
            await using var command = new SqlCommand(ddl, connection) { CommandTimeout = 0 };
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Vestige built index {Index} for [{Column}] in {ElapsedMs} ms.", name, field.Column, stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            try
            {
                if (connection.State == ConnectionState.Open)
                    await SqlAppLock.ReleaseAsync(connection, name).ConfigureAwait(false);
            }
            catch (SqlException)
            {
                // The session-owned lock is released when the connection closes anyway.
            }
        }
    }

    private async Task<bool> IndexExistsAsync(SqlConnection connection, string name, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(SqlServerSchemaSql.SelectIndexNames + " AND name = @index_name", connection);
        command.Parameters.Add(new SqlParameter("@object_name", SqlDbType.NVarChar, 512) { Value = layout.QualifiedTable });
        command.Parameters.Add(new SqlParameter("@index_name", SqlDbType.NVarChar, 128) { Value = name });
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task<int> GetEngineEditionAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(SqlServerSchemaSql.EngineEdition, connection);
        return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
}
```

In `src/Vestige.Sinks.SqlServer/SqlServerWriter.cs`, make the existing-table branch of `EnsureStorageAsync` survive replicas adding the same column at the same time. Replace:

```csharp
        var diff = await ReadDiffAsync(connection, layout, cancellationToken).ConfigureAwait(false);
        foreach (var field in diff.MissingColumns)
            await ExecuteAsync(connection, SqlServerSchemaSql.AddColumn(layout, field), cancellationToken).ConfigureAwait(false);

        return diff.MissingIndexes;
```

with:

```csharp
        var diff = await ReadDiffAsync(connection, layout, cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var field in diff.MissingColumns)
                await ExecuteAsync(connection, SqlServerSchemaSql.AddColumn(layout, field), cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number == 2705)
        {
            // Another replica added the column first; re-read and add whatever is still missing.
            diff = await ReadDiffAsync(connection, layout, cancellationToken).ConfigureAwait(false);
            foreach (var field in diff.MissingColumns)
                await ExecuteAsync(connection, SqlServerSchemaSql.AddColumn(layout, field), cancellationToken).ConfigureAwait(false);
        }

        return diff.MissingIndexes;
```

In `src/Vestige.Sinks.SqlServer/SqlServerSink.cs`:

1. Add these fields:

```csharp
    private readonly Func<SqlServerTableLayout, IReadOnlyList<PromotedField>, CancellationToken, Task>? _buildIndexes;
    private readonly CancellationTokenSource _backgroundCts = new();
    private Task _indexBuild = Task.CompletedTask;
```

2. Replace the internal constructor with:

```csharp
    internal SqlServerSink(
        IOptions<SqlServerSinkOptions> options,
        ILogger logger,
        ISqlServerWriter writer,
        TimeProvider timeProvider,
        Func<SqlServerTableLayout, IReadOnlyList<PromotedField>, CancellationToken, Task>? buildIndexes = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _options = options.Value;
        _logger = logger;
        _writer = writer;
        _timeProvider = timeProvider;
        _buildIndexes = buildIndexes;
    }

    /// <summary>Completes when background promoted-index builds started by <see cref="InitializeAsync"/> finish.</summary>
    internal Task IndexBuildCompletion => _indexBuild;
```

3. In `InitializeAsync`, replace:

```csharp
        if (_options.AutoCreate)
            await _writer.EnsureStorageAsync(cancellationToken).ConfigureAwait(false);
        else
            await _writer.VerifyStorageAsync(cancellationToken).ConfigureAwait(false);

        _layout = layout;
```

with:

```csharp
        IReadOnlyList<PromotedField> pendingIndexes = [];
        if (_options.AutoCreate)
            pendingIndexes = await _writer.EnsureStorageAsync(cancellationToken).ConfigureAwait(false);
        else
            await _writer.VerifyStorageAsync(cancellationToken).ConfigureAwait(false);

        _layout = layout;

        if (pendingIndexes.Count > 0)
            _indexBuild = RunBackgroundAsync(token => BuildIndexesAsync(layout, pendingIndexes, token));
```

4. Add these private methods:

```csharp
    private Task BuildIndexesAsync(SqlServerTableLayout layout, IReadOnlyList<PromotedField> pending, CancellationToken cancellationToken)
        => _buildIndexes is not null
            ? _buildIndexes(layout, pending, cancellationToken)
            : new SqlServerIndexBuilder(_options.ConnectionString, layout, _logger).BuildAsync(pending, cancellationToken);

    private Task RunBackgroundAsync(Func<CancellationToken, Task> work)
    {
        var token = _backgroundCts.Token;
        return Task.Run(async () =>
        {
            try
            {
                await work(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Vestige SQL Server sink background work failed.");
            }
        }, CancellationToken.None);
    }
```

5. Replace `DisposeAsync` with:

```csharp
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await _backgroundCts.CancelAsync().ConfigureAwait(false);
        await _indexBuild.ConfigureAwait(false);

        await WaitForInFlightWriteAsync(CancellationToken.None).ConfigureAwait(false);
        await _writer.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
        _backgroundCts.Dispose();
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Vestige.Tests -- --filter-class "Vestige.Tests.SqlServerSinkTests"`
Expected: PASS (8 tests).

Run: `dotnet test tests/Vestige.Sinks.IntegrationTests -- --filter-class "Vestige.Sinks.IntegrationTests.SqlServerIndexBuildTests" --filter-class "Vestige.Sinks.IntegrationTests.SqlServerStorageTests"`
Expected: PASS (9 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Vestige.Sinks.SqlServer tests/Vestige.Tests/SqlServerSinkTests.cs tests/Vestige.Sinks.IntegrationTests/SqlServerIndexBuildTests.cs
git commit -m "feat(sqlserver): build promoted indexes online in the background with an app lock"
```

---

### Task 9: SQL Server retention

**Files:**
- Create: `src/Vestige.Sinks.SqlServer/SqlServerRetentionSql.cs`, `src/Vestige.Sinks.SqlServer/SqlServerRetentionStore.cs`, `tests/Vestige.Tests/SqlServerRetentionSqlTests.cs`, `tests/Vestige.Sinks.IntegrationTests/SqlServerRetentionTests.cs`
- Modify: `src/Vestige.Sinks.SqlServer/SqlServerSink.cs`
- Test: `tests/Vestige.Tests/SqlServerSinkTests.cs` (add tests)

**Interfaces:**
- Consumes:
  - From Tasks 1–2: `RetentionPlanner.Plan`, `RetentionLoop`, `IRetentionStore`, `RetentionPass`, `RetentionCycleResult`.
  - From Task 5: `SqlServerTableLayout.ColumnFor`, `RetentionLockName`.
  - From Task 8: `SqlAppLock`, `SqlServerSink.RunBackgroundAsync`.
- Produces:
  - `internal static string SqlServerRetentionSql.DeleteBatch(SqlServerTableLayout, RetentionPass)`. Parameters: `@batch` int, `@period_seconds` int.
  - `internal sealed class SqlServerRetentionStore(string connectionString, SqlServerTableLayout layout) : IRetentionStore`
  - `SqlServerSink`:
    - `internal Task<RetentionCycleResult> RunRetentionCycleAsync(CancellationToken)` (throws `InvalidOperationException` when retention is off)
    - internal ctor gains an optional last parameter `Func<SqlServerTableLayout, IRetentionStore>? retentionStore = null`.

- [ ] **Step 1: Write the failing unit tests**

`tests/Vestige.Tests/SqlServerRetentionSqlTests.cs`:

```csharp
using Vestige.Retention;
using Vestige.Sinks.SqlServer;

namespace Vestige.Tests;

public sealed class SqlServerRetentionSqlTests
{
    private static SqlServerTableLayout Layout()
    {
        var options = new SqlServerSinkOptions { ConnectionString = "Server=.;Database=x" };
        options.PromotedFields.Add("audit_action", PromotedType.String(128));
        options.PromotedFields.Add("security.event", PromotedType.String(64), indexed: false);
        return new SqlServerTableLayout(options);
    }

    [Fact]
    public void DeleteBatch_WithoutExclusions_FiltersOnTimestampOnly()
    {
        Assert.Equal(
            "DELETE TOP (@batch) FROM [dbo].[wide_events] WHERE [timestamp_utc] < DATEADD(SECOND, -@period_seconds, SYSDATETIMEOFFSET())",
            SqlServerRetentionSql.DeleteBatch(Layout(), new RetentionPass(TimeSpan.FromDays(365), [])));
    }

    [Fact]
    public void DeleteBatch_WithExclusions_SparesRowsCarryingLongerRuleFields()
    {
        Assert.Equal(
            "DELETE TOP (@batch) FROM [dbo].[wide_events] WHERE [timestamp_utc] < DATEADD(SECOND, -@period_seconds, SYSDATETIMEOFFSET()) AND [audit_action] IS NULL AND [security_event] IS NULL",
            SqlServerRetentionSql.DeleteBatch(Layout(), new RetentionPass(TimeSpan.FromDays(30), ["audit_action", "security.event"])));
    }
}
```

Append to `tests/Vestige.Tests/SqlServerSinkTests.cs` inside the class (add `using Vestige.Retention;` at the top of the file):

```csharp
    [Fact]
    public async Task RetentionCycle_UsesPlannedPassesAgainstStore()
    {
        var store = new RecordingRetentionStore();
        var options = new SqlServerSinkOptions { ConnectionString = "Server=.;Database=x" };
        options.PromotedFields.Add("audit_action", PromotedType.String(128));
        options.Retention.Default = TimeSpan.FromDays(30);
        options.Retention.KeepLonger("audit_action", TimeSpan.FromDays(365));
        await using var sink = new SqlServerSink(
            Options.Create(options), new ListLogger<SqlServerSink>(), new FakeSqlServerWriter(), TimeProvider.System,
            retentionStore: _ => store);
        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        var result = await sink.RunRetentionCycleAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Acquired);
        Assert.Equal(new[] { TimeSpan.FromDays(30), TimeSpan.FromDays(365) }, store.Passes.Select(p => p.Period));
    }

    [Fact]
    public async Task RetentionCycle_WhenRetentionOff_Throws()
    {
        await using var sink = CreateSink(new FakeSqlServerWriter());
        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.RunRetentionCycleAsync(TestContext.Current.CancellationToken));
    }

    private sealed class RecordingRetentionStore : IRetentionStore
    {
        public List<RetentionPass> Passes { get; } = [];

        public Task<IAsyncDisposable?> TryAcquireLockAsync(CancellationToken cancellationToken)
            => Task.FromResult<IAsyncDisposable?>(new NoopLease());

        public Task<int> DeleteBatchAsync(RetentionPass pass, int batchSize, CancellationToken cancellationToken)
        {
            Passes.Add(pass);
            return Task.FromResult(0);
        }

        private sealed class NoopLease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
```

- [ ] **Step 2: Write the failing integration tests**

`tests/Vestige.Sinks.IntegrationTests/SqlServerRetentionTests.cs`:

```csharp
using Microsoft.Data.SqlClient;
using Vestige.Sinks.IntegrationTests.Support;
using Vestige.Sinks.SqlServer;

namespace Vestige.Sinks.IntegrationTests;

public sealed class SqlServerRetentionTests(SqlServerFixture db)
{
    private static void Configure(SqlServerSinkOptions o)
    {
        o.PromotedFields.Add("audit_action", PromotedType.String(128));
        o.Retention.Default = TimeSpan.FromDays(30);
        o.Retention.KeepLonger("audit_action", TimeSpan.FromDays(365));
    }

    private async Task SeedAsync(SqlServerSink sink, string table)
    {
        await sink.EmitBatchAsync(
        [
            TestEvents.Make(ev => ev.Set("seed", "telemetry_old")),
            TestEvents.Make(ev => ev.Set("seed", "telemetry_new")),
            TestEvents.Make(ev => { ev.Set("seed", "audit_mid"); ev.Set("audit_action", "tenant.create"); }),
            TestEvents.Make(ev => { ev.Set("seed", "audit_old"); ev.Set("audit_action", "tenant.remove"); }),
        ], TestContext.Current.CancellationToken);

        await SqlTestDb.ExecuteAsync(db.ConnectionString, $"""
            UPDATE dbo.[{table}] SET timestamp_utc = DATEADD(DAY, -40, SYSDATETIMEOFFSET()) WHERE JSON_VALUE(payload, '$.seed') = 'telemetry_old';
            UPDATE dbo.[{table}] SET timestamp_utc = DATEADD(DAY, -1, SYSDATETIMEOFFSET()) WHERE JSON_VALUE(payload, '$.seed') = 'telemetry_new';
            UPDATE dbo.[{table}] SET timestamp_utc = DATEADD(DAY, -100, SYSDATETIMEOFFSET()) WHERE JSON_VALUE(payload, '$.seed') = 'audit_mid';
            UPDATE dbo.[{table}] SET timestamp_utc = DATEADD(DAY, -400, SYSDATETIMEOFFSET()) WHERE JSON_VALUE(payload, '$.seed') = 'audit_old';
            """);
    }

    [Fact]
    public async Task Cycle_KeepsAuditLongerThanTelemetry()
    {
        db.SkipIfUnavailable();
        var table = SqlTestDb.UniqueTable();
        await using var sink = SinkFactory.SqlServer(db.ConnectionString, table, Configure);
        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedAsync(sink, table);

        var result = await sink.RunRetentionCycleAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Acquired);
        Assert.Equal(new[] { 1L, 1L }, result.DeletedPerPass);
        Assert.Equal("audit_mid,telemetry_new", await SqlTestDb.ScalarAsync<string>(db.ConnectionString,
            $"SELECT STRING_AGG(JSON_VALUE(payload, '$.seed'), ',') WITHIN GROUP (ORDER BY JSON_VALUE(payload, '$.seed')) FROM dbo.[{table}]"));
    }

    [Fact]
    public async Task Cycle_LockHeldByAnotherInstance_Skips()
    {
        db.SkipIfUnavailable();
        var table = SqlTestDb.UniqueTable();
        await using var sink = SinkFactory.SqlServer(db.ConnectionString, table, Configure);
        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedAsync(sink, table);
        await using var holder = new SqlConnection(db.ConnectionString);
        await holder.OpenAsync(TestContext.Current.CancellationToken);
        Assert.True(await SqlAppLock.TryAcquireAsync(holder, $"dbo_{table}_retention", TestContext.Current.CancellationToken));

        var result = await sink.RunRetentionCycleAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Acquired);
        Assert.Equal(4, await SqlTestDb.ScalarAsync<int>(db.ConnectionString, $"SELECT COUNT(*) FROM dbo.[{table}]"));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Vestige.Tests -- --filter-class "Vestige.Tests.SqlServerRetentionSqlTests" --filter-class "Vestige.Tests.SqlServerSinkTests"`
Expected: the build FAILS with `CS0103: The name 'SqlServerRetentionSql' does not exist in the current context`.

- [ ] **Step 4: Implement**

`src/Vestige.Sinks.SqlServer/SqlServerRetentionSql.cs`:

```csharp
using System.Text;
using Vestige.Retention;

namespace Vestige.Sinks.SqlServer;

internal static class SqlServerRetentionSql
{
    /// <summary>
    /// One batch of one pass. The timestamp range is served by the timestamp index; the
    /// <c>IS NULL</c> checks are residual predicates on promoted columns.
    /// </summary>
    public static string DeleteBatch(SqlServerTableLayout layout, RetentionPass pass)
    {
        var sql = new StringBuilder(
            $"DELETE TOP (@batch) FROM {layout.QualifiedTable} WHERE [timestamp_utc] < DATEADD(SECOND, -@period_seconds, SYSDATETIMEOFFSET())");
        foreach (var key in pass.KeepWhenPresent)
            sql.Append($" AND {SqlServerTableLayout.QuoteIdentifier(layout.ColumnFor(key))} IS NULL");
        return sql.ToString();
    }
}
```

`src/Vestige.Sinks.SqlServer/SqlServerRetentionStore.cs`:

```csharp
using System.Data;
using Microsoft.Data.SqlClient;
using Vestige.Retention;

namespace Vestige.Sinks.SqlServer;

internal sealed class SqlServerRetentionStore(string connectionString, SqlServerTableLayout layout) : IRetentionStore
{
    private const int DeleteCommandTimeoutSeconds = 300;

    public async Task<IAsyncDisposable?> TryAcquireLockAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (await SqlAppLock.TryAcquireAsync(connection, layout.RetentionLockName, cancellationToken).ConfigureAwait(false))
                return new Lease(connection, layout.RetentionLockName);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        await connection.DisposeAsync().ConfigureAwait(false);
        return null;
    }

    public async Task<int> DeleteBatchAsync(RetentionPass pass, int batchSize, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SqlServerRetentionSql.DeleteBatch(layout, pass), connection)
        {
            CommandTimeout = DeleteCommandTimeoutSeconds,
        };
        command.Parameters.Add(new SqlParameter("@batch", SqlDbType.Int) { Value = batchSize });
        command.Parameters.Add(new SqlParameter("@period_seconds", SqlDbType.Int) { Value = checked((int)pass.Period.TotalSeconds) });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class Lease(SqlConnection connection, string resource) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (connection.State == ConnectionState.Open)
                    await SqlAppLock.ReleaseAsync(connection, resource).ConfigureAwait(false);
            }
            catch (SqlException)
            {
                // Closing the connection releases the session-owned lock anyway.
            }

            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
```

In `src/Vestige.Sinks.SqlServer/SqlServerSink.cs`:

1. Add `using Vestige.Retention;` to the top of the file.
2. Add these fields:

```csharp
    private readonly Func<SqlServerTableLayout, IRetentionStore>? _retentionStore;
    private RetentionLoop? _retention;
    private Task _retentionRun = Task.CompletedTask;
```

3. Change the internal constructor signature to add a final optional parameter, and assign it:

```csharp
        Func<SqlServerTableLayout, IReadOnlyList<PromotedField>, CancellationToken, Task>? buildIndexes = null,
        Func<SqlServerTableLayout, IRetentionStore>? retentionStore = null)
```

```csharp
        _retentionStore = retentionStore;
```

4. At the end of `InitializeAsync` (after the index-build block), add:

```csharp
        if (_options.Retention.Enabled)
        {
            var store = _retentionStore?.Invoke(layout) ?? new SqlServerRetentionStore(_options.ConnectionString, layout);
            _retention = new RetentionLoop(RetentionPlanner.Plan(_options.Retention), store, _logger, _timeProvider);
            _retentionRun = RunBackgroundAsync(_retention.RunAsync);
        }
```

5. Add:

```csharp
    /// <summary>Run one retention cycle immediately (tests and operational tooling).</summary>
    internal Task<RetentionCycleResult> RunRetentionCycleAsync(CancellationToken cancellationToken)
        => _retention is { } retention
            ? retention.RunCycleAsync(cancellationToken)
            : throw new InvalidOperationException("Retention is not enabled for this sink.");
```

6. In `DisposeAsync`, replace `await _indexBuild.ConfigureAwait(false);` with:

```csharp
        await Task.WhenAll(_indexBuild, _retentionRun).ConfigureAwait(false);
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Vestige.Tests -- --filter-class "Vestige.Tests.SqlServerRetentionSqlTests" --filter-class "Vestige.Tests.SqlServerSinkTests"`
Expected: PASS (12 tests).

Run: `dotnet test tests/Vestige.Sinks.IntegrationTests -- --filter-class "Vestige.Sinks.IntegrationTests.SqlServerRetentionTests"`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Vestige.Sinks.SqlServer tests/Vestige.Tests tests/Vestige.Sinks.IntegrationTests/SqlServerRetentionTests.cs
git commit -m "feat(sqlserver): add tiered retention with batched deletes and an app lock"
```

---

### Task 10: Postgres retention

**Files:**
- Create: `src/Vestige.Sinks.Postgres/PostgresRetentionSql.cs`, `src/Vestige.Sinks.Postgres/PostgresRetentionStore.cs`, `tests/Vestige.Tests/PostgresRetentionSqlTests.cs`, `tests/Vestige.Sinks.IntegrationTests/Support/PostgresFixture.cs`, `tests/Vestige.Sinks.IntegrationTests/PostgresRetentionTests.cs`
- Modify: `src/Vestige.Sinks.Postgres/PostgresSinkOptions.cs`, `src/Vestige.Sinks.Postgres/PostgresSink.cs`, `src/Vestige.Sinks.Postgres/Properties/AssemblyInfo.cs`, `tests/Vestige.Sinks.IntegrationTests/AssemblyFixtures.cs`

**Interfaces:**
- Consumes: from Tasks 1–2, `RetentionOptions`, `RetentionPlanner`, `RetentionLoop`, `IRetentionStore`, `RetentionPass` and `RetentionCycleResult`.
- Produces:
  - `PostgresSinkOptions.Retention`
  - `internal static PostgresRetentionSql.DeleteBatch(string schema, string table, RetentionPass)`. Parameters: `@batch` int, `@period_seconds` double, `@k0..@kN` text.
  - `internal static PostgresRetentionSql.LockKey(string schema, string table)` → `"{prefix}_retention"`
  - `internal sealed class PostgresRetentionStore(string connectionString, PostgresSinkOptions options) : IRetentionStore`
  - `PostgresSink`:
    - `public PostgresSink(IOptions<PostgresSinkOptions>, ILogger<PostgresSink>)`
    - `internal Task<RetentionCycleResult> RunRetentionCycleAsync(CancellationToken)`
    - `DisposeAsync` cancels and awaits the loop.
  - `PostgresFixture` (same shape as `SqlServerFixture`).

- [ ] **Step 1: Write the failing tests**

`tests/Vestige.Tests/PostgresRetentionSqlTests.cs`:

```csharp
using Vestige.Retention;
using Vestige.Sinks.Postgres;

namespace Vestige.Tests;

public sealed class PostgresRetentionSqlTests
{
    [Fact]
    public void DeleteBatch_WithoutExclusions()
    {
        Assert.Equal(
            "DELETE FROM \"vestige\".\"wide_events\" WHERE ctid IN (SELECT ctid FROM \"vestige\".\"wide_events\" WHERE timestamp_utc < now() - make_interval(secs => @period_seconds) LIMIT @batch)",
            PostgresRetentionSql.DeleteBatch("vestige", "wide_events", new RetentionPass(TimeSpan.FromDays(365), [])));
    }

    [Fact]
    public void DeleteBatch_WithExclusions_UsesParameterisedJsonbExists()
    {
        Assert.Equal(
            "DELETE FROM \"vestige\".\"wide_events\" WHERE ctid IN (SELECT ctid FROM \"vestige\".\"wide_events\" WHERE timestamp_utc < now() - make_interval(secs => @period_seconds) AND NOT jsonb_exists(payload, @k0) AND NOT jsonb_exists(payload, @k1) LIMIT @batch)",
            PostgresRetentionSql.DeleteBatch("vestige", "wide_events", new RetentionPass(TimeSpan.FromDays(30), ["audit_action", "user.id"])));
    }

    [Fact]
    public void DeleteBatch_QuotesIdentifiers()
    {
        Assert.StartsWith(
            "DELETE FROM \"we\"\"ird\".\"t.x\"",
            PostgresRetentionSql.DeleteBatch("we\"ird", "t.x", new RetentionPass(TimeSpan.FromDays(1), [])));
    }

    [Fact]
    public void LockKey_FollowsIndexPrefixConvention()
    {
        Assert.Equal("vestige_wide_events_retention", PostgresRetentionSql.LockKey("vestige", "wide.events"));
    }
}
```

`tests/Vestige.Sinks.IntegrationTests/Support/PostgresFixture.cs`:

```csharp
using Testcontainers.PostgreSql;

namespace Vestige.Sinks.IntegrationTests.Support;

public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string? _unavailableReason;

    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder().WithImage("postgres:16").Build();
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }
        catch (Exception ex) when (Environment.GetEnvironmentVariable("VESTIGE_REQUIRE_DOCKER") != "1")
        {
            _unavailableReason = $"Postgres container unavailable: {ex.Message}";
        }
    }

    public void SkipIfUnavailable()
    {
        if (_unavailableReason is not null)
            Assert.Skip(_unavailableReason);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
```

Replace `tests/Vestige.Sinks.IntegrationTests/AssemblyFixtures.cs` with:

```csharp
using Vestige.Sinks.IntegrationTests.Support;

[assembly: AssemblyFixture(typeof(SqlServerFixture))]
[assembly: AssemblyFixture(typeof(PostgresFixture))]
```

`tests/Vestige.Sinks.IntegrationTests/PostgresRetentionTests.cs`:

```csharp
using Microsoft.Extensions.Options;
using Npgsql;
using Vestige.Sinks.IntegrationTests.Support;
using Vestige.Sinks.Postgres;

namespace Vestige.Sinks.IntegrationTests;

public sealed class PostgresRetentionTests(PostgresFixture db)
{
    private PostgresSink CreateSink(string table) => new(
        Options.Create(CreateOptions(table)),
        new ListLogger<PostgresSink>());

    private PostgresSinkOptions CreateOptions(string table)
    {
        var options = new PostgresSinkOptions { ConnectionString = db.ConnectionString, Schema = "vestige", Table = table };
        options.Retention.Default = TimeSpan.FromDays(30);
        options.Retention.KeepLonger("audit_action", TimeSpan.FromDays(365));
        return options;
    }

    private async Task SeedAsync(PostgresSink sink, string table)
    {
        await sink.EmitBatchAsync(
        [
            TestEvents.Make(ev => ev.Set("seed", "telemetry_old")),
            TestEvents.Make(ev => ev.Set("seed", "telemetry_new")),
            TestEvents.Make(ev => { ev.Set("seed", "audit_mid"); ev.Set("audit_action", "tenant.create"); }),
            TestEvents.Make(ev => { ev.Set("seed", "audit_old"); ev.Set("audit_action", "tenant.remove"); }),
        ], TestContext.Current.CancellationToken);

        await ExecuteAsync($"""
            UPDATE vestige."{table}" SET timestamp_utc = now() - interval '40 days' WHERE payload->>'seed' = 'telemetry_old';
            UPDATE vestige."{table}" SET timestamp_utc = now() - interval '1 day' WHERE payload->>'seed' = 'telemetry_new';
            UPDATE vestige."{table}" SET timestamp_utc = now() - interval '100 days' WHERE payload->>'seed' = 'audit_mid';
            UPDATE vestige."{table}" SET timestamp_utc = now() - interval '400 days' WHERE payload->>'seed' = 'audit_old';
            """);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string?> RemainingSeedsAsync(string table)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT string_agg(payload->>'seed', ',' ORDER BY payload->>'seed') FROM vestige.\"{table}\"", connection);
        return (string?)await command.ExecuteScalarAsync();
    }

    [Fact]
    public async Task Cycle_KeepsAuditLongerThanTelemetry()
    {
        db.SkipIfUnavailable();
        var table = SqlTestDb.UniqueTable();
        await using var sink = CreateSink(table);
        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedAsync(sink, table);

        var result = await sink.RunRetentionCycleAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Acquired);
        Assert.Equal(new[] { 1L, 1L }, result.DeletedPerPass);
        Assert.Equal("audit_mid,telemetry_new", await RemainingSeedsAsync(table));
    }

    [Fact]
    public async Task Cycle_AdvisoryLockHeldElsewhere_Skips()
    {
        db.SkipIfUnavailable();
        var table = SqlTestDb.UniqueTable();
        await using var sink = CreateSink(table);
        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedAsync(sink, table);
        await using var holder = new NpgsqlConnection(db.ConnectionString);
        await holder.OpenAsync(TestContext.Current.CancellationToken);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_lock(hashtext(@key))", holder))
        {
            lockCommand.Parameters.AddWithValue("key", PostgresRetentionSql.LockKey("vestige", table));
            await lockCommand.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var result = await sink.RunRetentionCycleAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Acquired);
        Assert.Equal("audit_mid,audit_old,telemetry_new,telemetry_old", await RemainingSeedsAsync(table));
    }

    [Fact]
    public async Task Initialize_InvalidRetention_Throws()
    {
        db.SkipIfUnavailable();
        var options = new PostgresSinkOptions { ConnectionString = db.ConnectionString, Table = SqlTestDb.UniqueTable() };
        options.Retention.KeepLonger("audit_action", TimeSpan.FromDays(365));
        await using var sink = new PostgresSink(Options.Create(options), new ListLogger<PostgresSink>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => sink.InitializeAsync(TestContext.Current.CancellationToken));
    }
}
```

The integration project needs `Npgsql` in scope for `NpgsqlConnection`. It comes transitively through `Vestige.Sinks.Postgres`, so no extra package reference is needed.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Vestige.Tests -- --filter-class "Vestige.Tests.PostgresRetentionSqlTests"`
Expected: the build FAILS with `CS0103: The name 'PostgresRetentionSql' does not exist in the current context`.

- [ ] **Step 3: Implement**

Replace `src/Vestige.Sinks.Postgres/Properties/AssemblyInfo.cs` with:

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Vestige.Tests")]
[assembly: InternalsVisibleTo("Vestige.Sinks.IntegrationTests")]
```

In `src/Vestige.Sinks.Postgres/PostgresSinkOptions.cs`, add `using Vestige.Retention;` at the top and add this property inside the class:

```csharp
    /// <summary>Tiered retention. Off until <see cref="RetentionOptions.Default"/> is set.</summary>
    public RetentionOptions Retention { get; } = new();
```

`src/Vestige.Sinks.Postgres/PostgresRetentionSql.cs`:

```csharp
using System.Text;
using Vestige.Retention;

namespace Vestige.Sinks.Postgres;

internal static class PostgresRetentionSql
{
    public static string DeleteBatch(string schema, string table, RetentionPass pass)
    {
        var qualified = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";
        var where = new StringBuilder("timestamp_utc < now() - make_interval(secs => @period_seconds)");
        for (var i = 0; i < pass.KeepWhenPresent.Count; i++)
            where.Append($" AND NOT jsonb_exists(payload, @k{i})");

        return $"DELETE FROM {qualified} WHERE ctid IN (SELECT ctid FROM {qualified} WHERE {where} LIMIT @batch)";
    }

    public static string LockKey(string schema, string table)
        => $"{schema}_{table}".Replace(".", "_", StringComparison.Ordinal) + "_retention";

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
```

`src/Vestige.Sinks.Postgres/PostgresRetentionStore.cs`:

```csharp
using Npgsql;
using NpgsqlTypes;
using Vestige.Retention;

namespace Vestige.Sinks.Postgres;

internal sealed class PostgresRetentionStore(string connectionString, PostgresSinkOptions options) : IRetentionStore
{
    private const int DeleteCommandTimeoutSeconds = 300;
    private readonly string _lockKey = PostgresRetentionSql.LockKey(options.Schema, options.Table);

    public async Task<IAsyncDisposable?> TryAcquireLockAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtext(@key))", connection);
            command.Parameters.AddWithValue("key", NpgsqlDbType.Text, _lockKey);
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
                return new Lease(connection, _lockKey);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        await connection.DisposeAsync().ConfigureAwait(false);
        return null;
    }

    public async Task<int> DeleteBatchAsync(RetentionPass pass, int batchSize, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(PostgresRetentionSql.DeleteBatch(options.Schema, options.Table, pass), connection)
        {
            CommandTimeout = DeleteCommandTimeoutSeconds,
        };
        command.Parameters.AddWithValue("batch", NpgsqlDbType.Integer, batchSize);
        command.Parameters.AddWithValue("period_seconds", NpgsqlDbType.Double, pass.Period.TotalSeconds);
        for (var i = 0; i < pass.KeepWhenPresent.Count; i++)
            command.Parameters.AddWithValue($"k{i}", NpgsqlDbType.Text, pass.KeepWhenPresent[i]);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class Lease(NpgsqlConnection connection, string key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtext(@key))", connection);
                command.Parameters.AddWithValue("key", NpgsqlDbType.Text, key);
                await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (NpgsqlException)
            {
                // Session-level advisory locks are released when the connection closes anyway.
            }

            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
```

In `src/Vestige.Sinks.Postgres/PostgresSink.cs`:

1. Add these usings:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vestige.Retention;
```

2. Add these fields:

```csharp
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _backgroundCts = new();
    private RetentionLoop? _retention;
    private Task _retentionRun = Task.CompletedTask;
```

3. Replace the constructors with:

```csharp
    public PostgresSink(IOptions<PostgresSinkOptions> options)
        : this(options, NullLogger<PostgresSink>.Instance)
    {
    }

    public PostgresSink(IOptions<PostgresSinkOptions> options, ILogger<PostgresSink> logger)
        : this(options, new NpgsqlPostgresCopyWriter(), logger, TimeProvider.System)
    {
    }

    internal PostgresSink(IOptions<PostgresSinkOptions> options, IPostgresCopyWriter writer)
        : this(options, writer, NullLogger<PostgresSink>.Instance, TimeProvider.System)
    {
    }

    internal PostgresSink(
        IOptions<PostgresSinkOptions> options,
        IPostgresCopyWriter writer,
        ILogger logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _options = options.Value;
        _writer = writer;
        _logger = logger;
        _timeProvider = timeProvider;
    }
```

4. Replace `InitializeAsync` with:

```csharp
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
            return;

        var passes = RetentionPlanner.Plan(_options.Retention);

        await _writer.InitializeAsync(_options, cancellationToken).ConfigureAwait(false);

        if (_options.AutoCreate)
            await _writer.EnsureStorageAsync(cancellationToken).ConfigureAwait(false);

        if (passes.Count > 0)
        {
            _retention = new RetentionLoop(
                passes,
                new PostgresRetentionStore(_options.ConnectionString, _options),
                _logger,
                _timeProvider);
            var token = _backgroundCts.Token;
            _retentionRun = Task.Run(() => _retention.RunAsync(token), CancellationToken.None);
        }
    }

    /// <summary>Run one retention cycle immediately (tests and operational tooling).</summary>
    internal Task<RetentionCycleResult> RunRetentionCycleAsync(CancellationToken cancellationToken)
        => _retention is { } retention
            ? retention.RunCycleAsync(cancellationToken)
            : throw new InvalidOperationException("Retention is not enabled for this sink.");
```

5. Replace `DisposeAsync` with:

```csharp
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await _backgroundCts.CancelAsync().ConfigureAwait(false);
        await _retentionRun.ConfigureAwait(false);

        await WaitForInFlightWriteAsync(CancellationToken.None).ConfigureAwait(false);
        await _writer.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
        _backgroundCts.Dispose();
    }
```

`RetentionLoop.RunAsync` swallows its own cancellation and logs other failures, so `_retentionRun` never faults.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Vestige.Tests`
Expected: all unit tests pass, including the 8 existing `PostgresSinkTests` and the 4 new `PostgresRetentionSqlTests`.

Run: `dotnet test tests/Vestige.Sinks.IntegrationTests -- --filter-class "Vestige.Sinks.IntegrationTests.PostgresRetentionTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Vestige.Sinks.Postgres tests/Vestige.Tests/PostgresRetentionSqlTests.cs tests/Vestige.Sinks.IntegrationTests
git commit -m "feat(postgres): add tiered retention with advisory-locked batched deletes"
```

---

### Task 11: Docs, version and CI

**Files:**
- Modify: `README.md`, `Directory.Build.props`, `.github/workflows/ci.yml`, `.github/workflows/publish.yml`

**Interfaces:**
- Consumes: the public API from Tasks 1–10.
- Produces: the 0.3.0 package version and documentation.

- [ ] **Step 1: Bump the version**

In `Directory.Build.props`, replace:

```xml
    <PackageVersion>0.2.0</PackageVersion>
    <AssemblyVersion>0.2.0.0</AssemblyVersion>
    <FileVersion>0.2.0.0</FileVersion>
```

with:

```xml
    <PackageVersion>0.3.0</PackageVersion>
    <AssemblyVersion>0.3.0.0</AssemblyVersion>
    <FileVersion>0.3.0.0</FileVersion>
```

- [ ] **Step 2: Require Docker in CI**

In both `.github/workflows/ci.yml` and `.github/workflows/publish.yml`, change the `dotnet test` step so it reads:

```yaml
      - run: dotnet test Vestige.slnx --configuration Release --no-build
        env:
          VESTIGE_REQUIRE_DOCKER: '1'
```

- [ ] **Step 3: Document the features**

In `README.md`, insert the following section immediately before the `## Sampling` heading:

````markdown
## Querying & Retention (SQL Server and PostgreSQL sinks)

### Promoted fields (SQL Server)

The SQL Server sink stores well-known fields as columns and everything else in `payload`. SQL Server
cannot index JSON, so promote the fields you filter on into typed, indexed columns:

```csharp
.AddSqlServerSink(o =>
{
    o.ConnectionString = cs;
    o.PromotedFields.Add("audit_action", PromotedType.String(128));
    o.PromotedFields.Add("tenant_id", PromotedType.Guid);
    o.PromotedFields.Add("user.id", PromotedType.String(64), column: "user_id", indexed: false);
});
```

- Types: `String(n)` (1–4000), `Guid`, `Int`, `Long`, `Bool`, `DateTimeOffset`.
- The column defaults to the key lower-cased, with non `[a-z0-9_]` characters replaced by `_`.
- Indexed fields get `([column], [timestamp_utc] DESC)`.
- A value that cannot be converted is written as `NULL` and logged as a warning at most once a minute per field. The original value is always kept in `payload`.
- **Upgrading an existing table:** missing columns are added as `NULL` (metadata only, instant). Indexes are then built **in the background** after startup, with `ONLINE = ON` on Enterprise/Developer, Azure SQL Database and Managed Instance. On other editions the sink logs the `CREATE INDEX` statement for you to run in a maintenance window, because an offline build would block inserts.
- Rows written before the upgrade keep `NULL` in promoted columns. Vestige never alters or drops a column.
- With `AutoCreate = false`, startup fails if a promoted column is missing.

### Retention (SQL Server and PostgreSQL)

Retention is **off by default**. Enable it with a default period, then keep events carrying a given field for longer:

```csharp
o.Retention.Default = TimeSpan.FromDays(30);
o.Retention.KeepLonger("audit_action", TimeSpan.FromDays(365));
```

- A background loop runs one minute after startup and then hourly.
- It deletes in batches of 4000 rows using the database clock.
- Only one instance purges at a time: `sp_getapplock` on SQL Server, `pg_try_advisory_lock` on PostgreSQL.
- On SQL Server, every `KeepLonger` key must be a promoted field, so the purge stays index-friendly.
````

- [ ] **Step 4: Run the full suite in Release**

Run: `dotnet build Vestige.slnx -c Release` and then `dotnet test Vestige.slnx -c Release --no-build`
Expected: the build reports 0 errors and 0 warnings in `src/`. All unit tests pass, plus the integration tests: 7 + 2 + 2 + 3 = 14.

- [ ] **Step 5: Commit**

```bash
git add README.md Directory.Build.props .github/workflows/ci.yml .github/workflows/publish.yml
git commit -m "docs: document promoted fields and retention; bump to 0.3.0"
```
