# Promoted Fields & Retention — Design

**Date:** 2026-10-16
**Target version:** 0.3.0
**Packages:** `Vestige` (shared retention type), `Vestige.Sinks.SqlServer`, `Vestige.Sinks.Postgres`

## 1. Motivation

The SQL Server sink stores about 12 well-known fields as real columns. Everything else, including business context such as `audit_action`, `actor_user_id` and `tenant_id`, lives only in `payload NVARCHAR(MAX)`. SQL Server has no JSON index, so filtering on those fields means running `JSON_VALUE` over every row in range. That doesn't scale for a table that grows by one row per request.

The first consumer is client-manager, which is building an internal ops audit page over `dbo.wide_events` (Azure SQL). It needs:

- Indexed filters on audit fields.
- A bounded table size, with audit rows kept far longer than request telemetry.

Postgres already has a GIN index on `payload jsonb`, so it only needs retention.

## 2. Scope

| Feature | SQL Server | Postgres |
|---|---|---|
| Promoted fields (typed columns + indexes) | Yes | No (GIN covers it) |
| Tiered retention | Yes | Yes |

Out of scope:
- Backfilling promoted columns for rows written before the upgrade.
- `RESUMABLE = ON` index builds (deferred until builds are measured to outlast deploy windows).
- Dropping or altering columns.
- Purge-interval or batch-size options.

## 3. Compatibility

All changes are additive. With no new options set, 0.3.0 issues exactly the same DDL and behaves exactly like 0.2.0. Retention is **off by default**.

## 4. Promoted fields (SQL Server)

### 4.1 API

```csharp
.AddSqlServerSink(o =>
{
    o.ConnectionString = cs;
    o.PromotedFields.Add("audit_action", PromotedType.String(128));
    o.PromotedFields.Add("actor_user_id", PromotedType.Guid);
    o.PromotedFields.Add("tenant_id", PromotedType.Guid);
    o.PromotedFields.Add("user.id", PromotedType.String(64), column: "user_id", indexed: false);
});
```

- **Key:** the wide-event field name exactly as passed to `ev.Set(...)`. The match is ordinal and case-sensitive.
- **Column:** defaults to the key lower-cased, with every character outside `[a-z0-9_]` replaced by `_`. It can be overridden with `column:`. It must not equal a fixed column (`id`, `event_id`, `timestamp_utc`, `service_name`, `service_version`, `deployment_environment`, `cloud_region`, `trace_id`, `span_id`, `outcome`, `duration_ms`, `http_status_code`, `http_method`, `http_path`, `error_type`, `payload`) or another promoted column. Max 128 characters.
- **Types:**

  | `PromotedType` | SQL type | Accepted runtime values |
  |---|---|---|
  | `String(n)`, 1 ≤ n ≤ 4000 | `NVARCHAR(n)` | `string`; any other value goes through `Convert.ToString(value, InvariantCulture)` |
  | `Guid` | `UNIQUEIDENTIFIER` | `Guid`; `string` parsed with `Guid.TryParse` |
  | `Int` | `INT` | `int`, `short`, `byte`; `long` within range |
  | `Long` | `BIGINT` | `long`, `int`, `short`, `byte` |
  | `Bool` | `BIT` | `bool` |
  | `DateTimeOffset` | `DATETIMEOFFSET(7)` | `DateTimeOffset`; `DateTime` (converted to UTC); ISO-8601 `string` |

- **`indexed`:** defaults to `true`. The index is `CREATE INDEX [{prefix}_{column}_idx] ON {table} ([{column}], [timestamp_utc] DESC) INCLUDE ([id])`, where `prefix` follows the existing convention (`{schema}_{table}`, dots replaced by `_`).
- An invalid configuration makes `InitializeAsync` throw with a message naming the field.

### 4.2 Write path

- `SqlServerEventRow` gains `object?[] PromotedValues`. Its order matches `PromotedFields`.
- `SqlServerEventRow.Map` converts each promoted field according to the table above. Strings are truncated to `n` with the existing surrogate-safe `Truncate`.
- **When conversion fails** (unsupported type, unparseable string, out of range), the value is written as `NULL` and nothing throws. A Warning is logged at most once per field per minute. Reason: a sink batch succeeds or fails as one unit, so throwing would lose the whole batch over one bad field. The original value always stays in `payload`.
- An absent field is written as `NULL` with no warning.
- The INSERT statement text, the parameter metadata, the bulk-copy column mappings and the `DataTable` schema are built **once** in `InitializeAsync` and cached. No column-list work happens per event.

### 4.3 Schema evolution (`AutoCreate = true`)

- **New table:** promoted columns are part of `CREATE TABLE`, and promoted indexes are created inline right after the existing ones.
- **Existing table:**
  1. Read `sys.columns` for the table.
  2. For each promoted column that's missing, run `ALTER TABLE ... ADD [col] <type> NULL`. This changes metadata only and is instant at any size.
  3. If a column with that name exists with a different type or length, throw from `InitializeAsync`. Never alter it.
  4. Put each `indexed` promoted field whose index is missing from `sys.indexes` on the pending-index list (§5).
- A field removed from config leaves its column in place, and the column stops being written.
- No backfill. Rows from before the upgrade keep NULL in promoted columns.

With `AutoCreate = false`, no DDL runs. If a promoted column is missing, `InitializeAsync` throws so the misconfiguration is visible.

## 5. Building indexes on existing tables (SQL Server)

`WideEventPipeline` awaits every sink's `InitializeAsync` before its consumer starts (`WideEventPipeline.cs`), and that wait happens during host startup. A long `CREATE INDEX` there would delay startup, let the 10k buffer fill and drop events, and risk a probe-kill/restart loop. So index builds on existing tables run **in the background**.

### 5.1 Flow

1. `InitializeAsync` finishes the column step (§4.3) and returns. The consumer starts.
2. The sink starts a background task, owned by the sink and cancelled by a sink-level `CancellationTokenSource`, that works through the pending-index list.
3. The task uses its own `SqlConnection` and `CommandTimeout = 0` (hardcoded; an index build has no sensible upper limit).

### 5.2 Edition handling

`SELECT SERVERPROPERTY('EngineEdition')` is read once.

| EngineEdition | Meaning | Action |
|---|---|---|
| 3 | Enterprise / Developer | `CREATE INDEX ... WITH (ONLINE = ON)` |
| 5 | Azure SQL Database | `CREATE INDEX ... WITH (ONLINE = ON)` |
| 8 | Azure SQL Managed Instance | `CREATE INDEX ... WITH (ONLINE = ON)` |
| other | Standard, Express, … | No build. Log a Warning that includes the exact DDL and the text "offline index build would block inserts; run it during a maintenance window". |

The edition-to-strategy decision is a pure function so it can be unit-tested with an injected edition value.

### 5.3 Concurrency across replicas

For each pending index:
1. `EXEC sp_getapplock @Resource = N'{prefix}_{column}_idx', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0`.
2. Return value < 0 means another replica is building. Log at Debug and skip.
3. Otherwise check `sys.indexes` again. If the index still doesn't exist, build it.
4. `sp_releaseapplock`. Session-owned locks are also released when the connection closes or the process crashes.

### 5.4 Observability and failure

- Information at start: index name, edition.
- Information at end: index name, duration.
- Error on failure. The task continues with the next index. Failure never affects writes, because promoted columns are written whether or not the index exists.
- `DisposeAsync` cancels the CTS and waits for the task. An interrupted online build rolls back, and the next start tries again.

While the index is missing, queries on the promoted column return correct results through a scan.

## 6. Tiered retention (SQL Server + Postgres)

### 6.1 API

The shared type lives in core `Vestige` (namespace `Vestige.Retention`) and is exposed as `Retention` on both `SqlServerSinkOptions` and `PostgresSinkOptions`:

```csharp
o.Retention.Default = TimeSpan.FromDays(30);                      // null = retention off (default)
o.Retention.KeepLonger("audit_action", TimeSpan.FromDays(365));
```

### 6.2 Validation (in `InitializeAsync`)

- `KeepLonger` requires `Default` to be set.
- Every `KeepLonger` period must be strictly greater than `Default`.
- Periods must be positive.
- The same key must not appear twice.
- **SQL Server:** each `KeepLonger` key must be a promoted field (otherwise: "retention rule on '{key}' requires a promoted field"). This keeps the purge index-friendly.
- **Postgres:** any key is allowed.

### 6.3 Semantics

- A rule *matches* a row when its field is present:
  - SQL Server: `[col] IS NOT NULL`.
  - Postgres: `jsonb_exists(payload, @key)`. Purge passes use this only as a negated residual predicate on a range the `timestamp_utc` index has already narrowed, so the GIN index isn't needed here.
- A row is kept for the longest period among the rules it matches, or for `Default` if it matches none.

### 6.4 Purge algorithm

- **Passes.** The distinct periods are sorted ascending as `p1 < p2 < … < pk`, with `p1 = Default`.
- **Pass i** deletes rows where `timestamp_utc < now − pi` **and** the row matches no rule whose period is greater than `pi`.
  - Example (30 d / audit 365 d):
    - Pass 1 deletes rows older than 30 d where `audit_action IS NULL`.
    - Pass 2 deletes rows older than 365 d.
- **Batching.** Each pass loops over batches until one affects 0 rows:
  - SQL Server: `DELETE TOP (4000) FROM ... WHERE ...`. 4000 stays under the 5000-lock escalation threshold.
  - Postgres: `DELETE FROM ... WHERE ctid IN (SELECT ctid FROM ... WHERE ... LIMIT 4000)`.
  - There is a 100 ms pause between batches.
- **Clock.** `now` is the database clock (`SYSUTCDATETIME()` / `now()`), not the app clock.
- **Schedule.** The first cycle runs 1 minute after `InitializeAsync`, then every hour.
- **Constants.** Batch size, pause, initial delay and interval are hardcoded, not options.

### 6.5 Single runner

- SQL Server: each cycle takes `sp_getapplock @Resource = N'{prefix}_retention', @LockOwner = 'Session', @LockTimeout = 0` on a dedicated connection. Postgres uses `pg_try_advisory_lock(hashtext('{prefix}_retention'))`.
- If the lock isn't acquired, the cycle is skipped and logged at Debug.
- The lock is released at the end of the cycle.

### 6.6 Lifecycle and observability

- The loop starts at the end of `InitializeAsync` when `Retention.Default` is set.
- With `AutoCreate = false`, the sink first checks that the rule columns exist. If one is missing, it logs an Error and disables retention. The sink itself keeps writing.
- Information per cycle when rows were deleted: count for each pass and total duration. Error on failure. The loop keeps running.
- `DisposeAsync` cancels the loop through the same sink-level CTS as the index builder and waits for it.

### 6.7 Known cost

Rows past a shorter cutoff that a longer rule keeps (for example audit rows between 30 d and 365 d) are scanned again on every pass for a shorter period. This is acceptable at audit volume, which is a small fraction of telemetry. If it ever matters, the fix is a filtered index on the rule column.

## 7. Testing

### 7.1 Unit tests (`tests/Vestige.Tests`, fakes)

- Promoted-field validation:
  - Column name sanitizing.
  - Collisions with fixed or promoted columns.
  - `String(n)` bounds and column length limit.
- `Map` conversion for each type: native value, string-parsed value, truncation, failed conversion gives NULL plus a rate-limited warning, absent field gives NULL.
- Cached column lists: the INSERT text and bulk-copy mappings include promoted columns in order.
- Edition → strategy decision for 3/5/8/other.
- Retention validation rules (§6.2).
- Pass planning: rules turn into the ordered passes and predicates of §6.4.

### 7.2 Integration tests (new `tests/Vestige.Sinks.IntegrationTests`, Testcontainers)

Images: `mcr.microsoft.com/mssql/server:2022-latest` (Developer, EngineEdition 3) and `postgres:16`. Tests are skipped when Docker isn't available. CI runs them on ubuntu runners.

**SQL Server**
- A new table is created with promoted columns and indexes.
- **Upgrade from 0.2.0:**
  - Create the table with 0.2.0's literal DDL (copied into the test, not generated by new code) and seed rows.
  - Start the 0.3.0 sink. Columns are added, the background online index appears, and old rows have NULL promoted values.
- The single-row INSERT path and the bulk-copy path both write promoted values.
- A type mismatch on an existing column makes `InitializeAsync` throw.
- Two sink instances on the same table: exactly one builds each index, and no errors are logged.
- Retention:
  - Seed back-dated rows with and without `audit_action`.
  - Run one cycle.
  - Telemetry past 30 d is gone, audit rows past 30 d remain, and audit rows past 365 d are gone.

**Postgres**
- Retention cycle with a `jsonb_exists` rule, same assertions as above.

### 7.3 Coverage gap

Containers only provide EngineEdition 3, so the Standard/Express "warn and skip" path is covered only by the unit test of the strategy decision.

## 8. Release

- Version 0.3.0 (`Directory.Build.props`).
- The README gets a "Querying & retention" section: promoted fields, index build behaviour by edition, retention tiers, and the fact that retention is off by default.
- Test-only dependency: `Testcontainers.MsSql`, `Testcontainers.PostgreSql`.

## 9. Follow-up in client-manager (not part of this spec)

- Bump Vestige to 0.3.0. This also brings 0.2.0's unhandled-exception capture.
- Configure promoted `audit_action`, `actor_user_id`, `actor_api_key_id` and `tenant_id`. Retention: telemetry 30 d, audit 365 d (final numbers decided there).
- Continue with the events page spec. Decisions already made:
  - Audit events by default, with a toggle for all requests.
  - Any client-manager user can view (no role yet).
  - Audit SSO login and `RecoverProvisioning`.
  - `AlwaysKeepWhen(e => e.Has("audit_action"))`.
  - 7-day default window, 30-day max.
