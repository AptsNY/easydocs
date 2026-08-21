# Document List Sorting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let someone sort the dashboard's document tiles by last updated, name, or creation date in
either direction, with the choice held in the URL and the default becoming last-updated-first.

**Architecture:** `GET /api/v1/documents` gains `sort` and `order`. The keyset cursor grows a
one-byte tag naming which column its key came from, so a cursor cannot be silently compared against
the wrong column. The shared paging mechanics (probe with `limit + 1`, trim, mint the next cursor)
move into `Pagination.ProbeAsync`; each sort's `WHERE`/`ORDER BY` is written out explicitly because
EF cannot compose a caller-supplied key selector into a predicate. `Dashboard.tsx` gets one native
`<select>` whose state lives in `useSearchParams`.

**Tech Stack:** .NET 10 minimal APIs, EF Core + Npgsql, xUnit + Testcontainers, React 19 +
react-router, Playwright.

**Spec:** `docs/superpowers/specs/2026-08-21-document-list-sorting-design.md`

---

## Before You Start

Read these three files end to end. Every task below assumes you have.

- `src/EasyDocs.Api/Api/Pagination.cs` — 70 lines. The entire current cursor implementation.
- `src/EasyDocs.Api/Documents/DocumentEndpoints.cs:123-149` — `ListDocuments`, the only method whose
  query changes.
- `web/src/routes/Dashboard.tsx` — 291 lines. The comments in it explain why search is server-side;
  the same reasoning is why sorting is server-side.

**Running the backend tests requires Docker** — the suite uses Testcontainers to boot a real
PostgreSQL 16. `dotnet test` with no Docker daemon fails at fixture setup, not at your assertion.

**Running the e2e tests requires the API on `:8080`.** Playwright starts Vite itself (see
`web/playwright.config.ts`) but not the API. Either `docker compose up -d` or
`dotnet run --project src/EasyDocs.Api`.

**Conventions this codebase holds you to, which the review will check:**

- Comments explain *why*, never *what*. Read the existing comments in `Pagination.cs` and
  `Dashboard.tsx` for the register. Do not add a comment that restates the line below it.
- A deliberate shortcut with a known ceiling gets a `ponytail:` comment naming the ceiling and the
  upgrade path. There are examples in `Dashboard.tsx` and `DocumentListProjection.cs`.
- Test names are sentences: `A_tile_shows_the_current_number_the_modified_time_and_the_last_author`.
- Every repeated control in the UI carries a `.visually-hidden` suffix saying which row it acts on.

## File Structure

| File | Change | Responsibility after this plan |
| --- | --- | --- |
| `src/EasyDocs.Api/Api/Pagination.cs` | Modify | Cursor encode/decode (now tagged), limit clamping, the `ProbeAsync` probe, and the `IKeyed` `PageAsync` re-based on it |
| `src/EasyDocs.Api/Documents/DocumentEndpoints.cs` | Modify `ListDocuments` only | Parse `sort`/`order`, project the three sort keys, apply the keyset, mint the cursor |
| `src/EasyDocs.Api/Documents/DocumentListProjection.cs` | **No change** | Still builds the tile payload; still reports `updatedAt: null` for a version-less document |
| `tests/EasyDocs.Api.Tests/PaginationTests.cs` | Modify | Cursor round trips, `ProbeAsync`, existing `PageAsync` regression |
| `tests/EasyDocs.Api.Tests/DocumentListTests.cs` | Modify | Sort order, sorted paging, and the two 400s |
| `web/src/routes/Dashboard.tsx` | Modify | The sort select, URL state, and passing `sort`/`order` to `load` |
| `web/src/index.css` | Modify | Two rules: the label's inline row, and its mobile width |
| `web/e2e/dashboard.spec.ts` | Modify | One spec: pick a sort, assert order, assert it survives a reload |

Nothing is created. Nothing else is touched.

---

## Task 1: Tagged cursor encode/decode

The cursor stops being a fixed 24-byte `(ticks, guid)` struct and becomes
`1B tag ‖ key ‖ 16B Guid`, with the key length derived from the payload length. `Encode`/`Decode`
keep their names but change shape, so this task also updates the one existing test that asserts the
old shape.

**Files:**
- Modify: `src/EasyDocs.Api/Api/Pagination.cs:1-48` (the `Encode`/`Decode` pair)
- Test: `tests/EasyDocs.Api.Tests/PaginationTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `tests/EasyDocs.Api.Tests/PaginationTests.cs`. `Pagination` is already imported there via
`using EasyDocs.Api.Api;`.

```csharp
    [Fact]
    public void A_time_cursor_round_trips()
    {
        var when = new DateTimeOffset(2026, 8, 21, 12, 34, 56, TimeSpan.Zero);
        var id = Guid.NewGuid();

        var decoded = Pagination.Decode(Pagination.EncodeTime(7, when, id));

        Assert.NotNull(decoded);
        Assert.Equal(7, decoded!.Tag);
        Assert.Equal(id, decoded.Id);
        Assert.Equal(when, Pagination.AsTime(decoded));
    }

    // A name key is variable-length and may be multibyte, which is the whole reason the payload
    // carries no length field — everything after the tag and before the trailing 16 bytes IS the key.
    [Fact]
    public void A_text_cursor_round_trips_including_multibyte_names()
    {
        var id = Guid.NewGuid();
        const string name = "bail à loyer — 賃貸借契約";

        var decoded = Pagination.Decode(Pagination.EncodeText(2, name, id));

        Assert.NotNull(decoded);
        Assert.Equal(2, decoded!.Tag);
        Assert.Equal(id, decoded.Id);
        Assert.Equal(name, Pagination.AsText(decoded));
    }

    // A cursor is worthless if a client's garbage throws instead of restarting the list: Decode
    // returning null means "no WHERE clause", which means page one.
    [Theory]
    [InlineData("")]
    [InlineData("not-base64url-!!!")]
    [InlineData("AAAA")] // decodes, but too short to hold a tag and a Guid
    public void An_unusable_cursor_decodes_to_null_rather_than_throwing(string cursor)
    {
        Assert.Null(Pagination.Decode(cursor));
    }

    // A zero-length key is legal (an empty document name lower-cases to ""), and must not be
    // mistaken for a truncated payload.
    [Fact]
    public void An_empty_text_key_is_a_valid_cursor()
    {
        var id = Guid.NewGuid();

        var decoded = Pagination.Decode(Pagination.EncodeText(2, "", id));

        Assert.NotNull(decoded);
        Assert.Equal("", Pagination.AsText(decoded!));
        Assert.Equal(id, decoded!.Id);
    }
```

- [ ] **Step 2: Run them to verify they fail to compile**

```bash
dotnet test --filter "FullyQualifiedName~PaginationTests" 2>&1 | tail -20
```

Expected: build error — `EncodeTime`, `EncodeText`, `AsTime`, `AsText` and `CursorKey.Tag` do not
exist.

- [ ] **Step 3: Replace `Encode`/`Decode` in `Pagination.cs`**

Delete the current `Encode` and `Decode` and put this in their place. Keep `PagedResult`,
`DefaultLimit`, `MaxLimit`, `ClampLimit` and `Descending` exactly as they are.

```csharp
// A cursor names the column its key came from. Without the tag, a client that changes ?sort= while
// holding a cursor gets a name compared against a timestamp — a silently wrong page rather than an
// error. Layout: 1B tag || key || 16B Guid. No length field: there is exactly one variable-length
// field, so the key is everything between the tag and the trailing Guid.
//
// Cursors minted by a previous release do not decode. That is deliberate: their 24-byte payload is
// indistinguishable from a new cursor carrying a 7-byte name key ("invoice"), so honouring them
// would misread a real page. Decode returns null for anything unusable and a null cursor means no
// WHERE clause, so the worst case is a client's next page restarting at the top.
public sealed record CursorKey(byte Tag, byte[] Key, Guid Id);

// The tag PageAsync's own (CreatedAt, Id) cursors carry. Any endpoint sorting on creation time uses
// it, so a `created` cursor means the same thing everywhere it appears.
public const byte CreatedTag = 0;

public static string Encode(byte tag, ReadOnlySpan<byte> key, Guid id)
{
    var buf = new byte[1 + key.Length + 16];
    buf[0] = tag;
    key.CopyTo(buf.AsSpan(1));
    id.TryWriteBytes(buf.AsSpan(1 + key.Length));
    return Base64Url.EncodeToString(buf);
}

public static CursorKey? Decode(string? cursor)
{
    if (string.IsNullOrEmpty(cursor)) return null;
    try
    {
        var b = Base64Url.DecodeFromChars(cursor);
        if (b.Length < 17) return null;
        return new CursorKey(b[0], b[1..^16], new Guid(b.AsSpan(b.Length - 16, 16)));
    }
    catch (FormatException) { return null; }
    catch (ArgumentException) { return null; }
}

public static string EncodeTime(byte tag, DateTimeOffset time, Guid id)
{
    Span<byte> key = stackalloc byte[8];
    BitConverter.TryWriteBytes(key, time.UtcTicks);
    return Encode(tag, key, id);
}

// Null for a key that is not a timestamp at all — a name cursor replayed on a time sort reaches here
// only if the tag check upstream was skipped, and must not become a garbage DateTimeOffset.
public static DateTimeOffset? AsTime(CursorKey key)
{
    if (key.Key.Length != 8) return null;
    var ticks = BitConverter.ToInt64(key.Key, 0);
    if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks) return null;
    return new DateTimeOffset(ticks, TimeSpan.Zero);
}

public static string EncodeText(byte tag, string text, Guid id) =>
    Encode(tag, Encoding.UTF8.GetBytes(text), id);

public static string AsText(CursorKey key) => Encoding.UTF8.GetString(key.Key);
```

Add `using System.Text;` to the top of the file. `System.Buffers.Text` is already imported.

- [ ] **Step 4: Fix `PageAsync` to compile against the new pair**

`PageAsync` currently calls `Decode(cursor) is { } k` and reads `k.Time`, and calls
`Encode((last.CreatedAt, last.Id))`. Change those two lines only:

```csharp
        if (Decode(cursor) is { Tag: CreatedTag } c && AsTime(c) is { } t)
            query = descending
                ? query.Where(x => x.CreatedAt < t || (x.CreatedAt == t && x.Id.CompareTo(c.Id) < 0))
                : query.Where(x => x.CreatedAt > t || (x.CreatedAt == t && x.Id.CompareTo(c.Id) > 0));
```

and

```csharp
        return new PagedResult<T>(rows, EncodeTime(CreatedTag, last.CreatedAt, last.Id));
```

The `{ Tag: CreatedTag }` pattern is load-bearing and is **not** the 400 that Task 3 adds. Without it,
an 8-byte name key passes `AsTime`: `DateTimeOffset.MaxValue.UtcTicks` is `0x2BCA2875F4373FFF` and
`BitConverter` is little-endian, so the key's last byte is the most significant, and any 8-byte key
ending in a byte <= `0x2B` — any 8-character name ending in a space or common punctuation — decodes to
a year-7398 timestamp. On the ascending call sites (`ListVersions`, `ApprovalEndpoints`) that WHERE
matches nothing, and the response is empty *with* `nextCursor: null`, which a client cannot tell from
end-of-list. A foreign tag must land on the no-WHERE path (page one), which is what this pattern does.
Do **not** turn it into a 400 here: these endpoints have no `sort` parameter for a tag to disagree with.

- [ ] **Step 5: Run the whole pagination suite**

```bash
dotnet test --filter "FullyQualifiedName~PaginationTests" 2>&1 | tail -20
```

Expected: PASS, including the pre-existing `Documents_list_paginates_with_cursor` and the versions
test in that file. If one of those fails, the change to `PageAsync` is wrong — fix it here rather
than in a later task.

- [ ] **Step 6: Confirm no other call site broke**

```bash
dotnet build 2>&1 | grep -E "error|Warning.*CS" | head -20
```

Expected: no output. The other five `PageAsync` call sites (`PublishEndpoints`, `ShareEndpoints`,
`AuditEndpoints`, `ApprovalEndpoints`, `DocumentEndpoints.ListVersions`) pass through unchanged
because the signature did not change.

- [ ] **Step 7: Commit**

```bash
git add src/EasyDocs.Api/Api/Pagination.cs tests/EasyDocs.Api.Tests/PaginationTests.cs
git commit -m "feat(api): tag the pagination cursor with the column it sorted on

A cursor that does not say which column produced its key cannot be checked
against a caller's ?sort=, so changing sort mid-pagination would compare a
name against a timestamp. The payload becomes 1B tag || key || 16B Guid,
with the key length derived, so a variable-length key fits.

Cursors from a previous release no longer decode: a 24-byte payload is
ambiguous with a new cursor carrying a 7-byte name key. Decode already
returns null for anything unusable, which means page one."
```

---

## Task 2: The `ProbeAsync` helper

One implementation of "read `limit + 1` rows, trim the probe, mint the next cursor from the last kept
row" — the only part of keyset paging that does not depend on the sort key. `PageAsync` is re-based on
it so there is exactly one copy.

**Files:**
- Modify: `src/EasyDocs.Api/Api/Pagination.cs` (`PageAsync`, plus the new helper)
- Test: `tests/EasyDocs.Api.Tests/PaginationTests.cs`

- [ ] **Step 1: Write the failing test**

This one exercises the probe through the API rather than in isolation, because the interesting
behaviour is "the last page has a null cursor" and that needs a real query. Add to
`PaginationTests.cs`:

```csharp
    // The probe reads limit+1 to learn whether a next page exists, then must not leak the probe row
    // into the response. Off by one here means a duplicated document on every "Load more".
    [Fact]
    public async Task A_page_returns_exactly_the_limit_and_the_last_page_has_no_cursor()
    {
        var c = await AuthedClientAsync();
        var folderId = (await (await c.PostAsJsonAsync("/api/v1/folders", new { name = "Probe" }))
            .Content.ReadFromJsonAsync<CreateDto>())!.Id;
        for (var i = 0; i < 3; i++)
            (await c.PostAsJsonAsync("/api/v1/documents", new { name = $"P{i}", folderId }))
                .EnsureSuccessStatusCode();

        var first = await c.GetFromJsonAsync<Page<DocItem>>(
            $"/api/v1/documents?folderId={folderId}&limit=2");
        Assert.Equal(2, first!.Items.Count);
        Assert.NotNull(first.NextCursor);

        var second = await c.GetFromJsonAsync<Page<DocItem>>(
            $"/api/v1/documents?folderId={folderId}&limit=2&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Single(second!.Items);
        Assert.Null(second.NextCursor);
    }
```

- [ ] **Step 2: Run it**

```bash
dotnet test --filter "FullyQualifiedName~A_page_returns_exactly_the_limit" 2>&1 | tail -20
```

Expected: PASS already — this is the behaviour `PageAsync` has today. It is a **regression guard**,
written before the refactor so that the refactor cannot break it unnoticed. If it fails now, stop and
work out why before touching `PageAsync`.

- [ ] **Step 3: Extract `ProbeAsync`**

Add to `Pagination.cs`:

```csharp
// The half of keyset paging that does not care what it sorted on: read one row past the limit to
// learn whether there is a next page, drop it, and mint the cursor from the last row kept.
//
// The caller keeps its own WHERE and ORDER BY. Owning those here would mean composing a
// caller-supplied Expression<Func<T, TKey>> into a predicate, which EF cannot invoke — doing it
// properly needs hand-built Expression.GreaterThan trees plus a separate string.CompareTo path, more
// code than the explicit Where clauses it would replace and much harder to read.
public static async Task<PagedResult<T>> ProbeAsync<T>(
    IQueryable<T> ordered, int? limit, Func<T, string> nextCursor, CancellationToken ct)
{
    var take = ClampLimit(limit);
    var rows = await ordered.Take(take + 1).ToListAsync(ct);
    if (rows.Count <= take) return new PagedResult<T>(rows, null);

    rows.RemoveAt(rows.Count - 1);
    return new PagedResult<T>(rows, nextCursor(rows[^1]));
}
```

- [ ] **Step 4: Re-base `PageAsync` on it**

`PageAsync`'s body after the `Where`/`OrderBy` block becomes one line:

```csharp
        return await ProbeAsync(query, limit, x => EncodeTime(CreatedTag, x.CreatedAt, x.Id), ct);
```

Delete the `take`, `rows`, `RemoveAt` and `last` lines it replaces. `ClampLimit` is now called inside
`ProbeAsync`, so remove the local `take` variable entirely.

- [ ] **Step 5: Run the full backend suite**

```bash
dotnet test 2>&1 | tail -20
```

Expected: PASS. Every paginated endpoint in the product routes through the line you just changed, so
run the whole suite, not a filter.

- [ ] **Step 6: Commit**

```bash
git add src/EasyDocs.Api/Api/Pagination.cs tests/EasyDocs.Api.Tests/PaginationTests.cs
git commit -m "refactor(api): extract the keyset probe from PageAsync

ProbeAsync owns the limit+1 read, the trim, and minting the next cursor.
The caller keeps its WHERE and ORDER BY, which is what differs per sort key
and what EF cannot compose from a supplied key selector."
```

---

## Task 3: `sort` and `order` on `GET /documents` — rejection paths first

Parameter parsing and the two 400s, before any sorting works. Doing the rejections first means the
sort branches in Task 4 can assume a valid tag and a compatible cursor.

**Files:**
- Modify: `src/EasyDocs.Api/Documents/DocumentEndpoints.cs:123-149`
- Test: `tests/EasyDocs.Api.Tests/DocumentListTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `DocumentListTests.cs`:

```csharp
    // Unlike ?order=, a bad ?sort= is not safely ignorable: it decides which column the cursor's key
    // means, so falling back would page a client against a column it did not ask for.
    [Fact]
    public async Task An_unknown_sort_is_rejected_rather_than_silently_ignored()
    {
        var acct = await _f.RegisterAsync();

        var res = await acct.Client.GetAsync("/api/v1/documents?sort=nonsense");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("created")]
    [InlineData("updated")]
    [InlineData("name")]
    public async Task Every_documented_sort_is_accepted(string sort)
    {
        var acct = await _f.RegisterAsync();

        var res = await acct.Client.GetAsync($"/api/v1/documents?sort={sort}");

        res.EnsureSuccessStatusCode();
    }

    // The cursor carries the column it was built from, so replaying a name cursor under a time sort
    // is caught. Without this the WHERE would compare a name against a timestamp and quietly return
    // the wrong page.
    [Fact]
    public async Task A_cursor_from_one_sort_is_rejected_under_another()
    {
        var acct = await _f.RegisterAsync();
        var folderId = await acct.Client.CreateFolderAsync("Mismatch");
        for (var i = 0; i < 3; i++)
            await acct.Client.CreateDocAsync($"M{i}", folderId);

        var byName = await acct.Client.GetFromJsonAsync<Page>(
            $"/api/v1/documents?folderId={folderId}&sort=name&limit=2");
        Assert.NotNull(byName!.NextCursor);

        var replayed = await acct.Client.GetAsync(
            $"/api/v1/documents?folderId={folderId}&sort=updated&limit=2&cursor={Uri.EscapeDataString(byName.NextCursor!)}");

        Assert.Equal(HttpStatusCode.BadRequest, replayed.StatusCode);
    }
```

Add `using System.Net;` to the file's usings.

`CreateFolderAsync` does not exist yet. Add it to `tests/EasyDocs.Api.Tests/TestAuth.cs` next to
`CreateDocAsync`, following the same shape:

```csharp
    public static async Task<Guid> CreateFolderAsync(this HttpClient c, string name, Guid? parentId = null)
    {
        var res = await c.PostAsJsonAsync("/api/v1/folders", new { name, parentId });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<IdDto>())!.Id;
    }
```

(`CreateFolderRequest` is `(string? Name, Guid? ParentId)`, so those field names are right.)

Every test in this task and the next scopes its documents to a **fresh folder**. The suite shares one
database, so a query over the whole org would see documents other tests created concurrently and the
order assertions would be flaky.

- [ ] **Step 2: Run them to verify they fail**

```bash
dotnet test --filter "FullyQualifiedName~DocumentListTests" 2>&1 | tail -25
```

Expected: `An_unknown_sort_is_rejected` FAILS with `200 != 400` (an unknown query parameter is
ignored today), and `A_cursor_from_one_sort_is_rejected` FAILS. `Every_documented_sort_is_accepted`
PASSES for the wrong reason — the parameters are simply ignored.

- [ ] **Step 3: Add the parameters and the guards**

In `DocumentEndpoints.cs`, extend `ListDocuments`'s signature with the two parameters, and add the
tag table above the method:

```csharp
    // Which column a cursor's key came from. `created` reuses Pagination.CreatedTag so a creation-time
    // cursor means the same thing here as it does on every other paginated endpoint.
    private const byte SortCreated = Pagination.CreatedTag;
    private const byte SortUpdated = 1;
    private const byte SortName = 2;

    private static byte? SortTag(string? sort) => (sort ?? "").ToLowerInvariant() switch
    {
        "" or "created" => SortCreated,
        "updated" => SortUpdated,
        "name" => SortName,
        _ => null,
    };
```

Then at the top of `ListDocuments`, before the membership query:

```csharp
        if (SortTag(sort) is not { } tag)
            return Problem.Of(400, "Invalid sort", "sort must be one of: created, updated, name.");

        var after = Pagination.Decode(cursor);
        if (after is not null && after.Tag != tag)
            return Problem.Of(400, "Cursor mismatch",
                "This cursor was issued for a different sort order. Drop the cursor when you change sort.");
```

Signature becomes:

```csharp
    private static async Task<IResult> ListDocuments(
        HttpContext ctx, EasyDocsDbContext db, Guid? folderId, string? q, string? cursor, int? limit,
        bool? trashed, string? sort, string? order)
```

Leave the existing `Pagination.PageAsync(query, cursor, limit, descending: false, ...)` call alone
for now — Task 4 replaces it. `tag`, `after` and `order` are unused this task; that is fine and the
compiler will not complain about unused locals that are read by nothing.

- [ ] **Step 4: Run the tests**

```bash
dotnet test --filter "FullyQualifiedName~DocumentListTests" 2>&1 | tail -25
```

Expected: all PASS. `A_cursor_from_one_sort_is_rejected` passes because a `sort=name` request still
mints a `CreatedTag` cursor today, and `SortUpdated != SortCreated`. It will keep passing for the
right reason after Task 4.

- [ ] **Step 5: Commit**

```bash
git add src/EasyDocs.Api/Documents/DocumentEndpoints.cs tests/EasyDocs.Api.Tests/
git commit -m "feat(api): accept sort and order on GET /documents, reject bad pairs

An unknown sort is a 400 rather than a fallback, and a cursor whose tag
disagrees with the requested sort is a 400 rather than a wrong page. The
sorting itself lands next."
```

---

## Task 4: Sort the documents query

The substance. One projection carrying all three sort keys, one keyset function covering three
columns × two directions, and the cursor minted from whichever key sorted.

**Files:**
- Modify: `src/EasyDocs.Api/Documents/DocumentEndpoints.cs` (`ListDocuments`)
- Test: `tests/EasyDocs.Api.Tests/DocumentListTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
    // Names are compared lower-cased. Under the default Postgres collation a raw ORDER BY name puts
    // every capital ahead of every lowercase letter, so "Zebra" would sort before "apple".
    [Fact]
    public async Task Sorting_by_name_is_case_insensitive()
    {
        var acct = await _f.RegisterAsync();
        var folderId = await acct.Client.CreateFolderAsync("Alphabet");
        await acct.Client.CreateDocAsync("Zebra", folderId);
        await acct.Client.CreateDocAsync("apple", folderId);
        await acct.Client.CreateDocAsync("Mango", folderId);

        var asc = await acct.Client.GetFromJsonAsync<Page>(
            $"/api/v1/documents?folderId={folderId}&sort=name&order=asc&limit=100");
        Assert.Equal(new[] { "apple", "Mango", "Zebra" }, asc!.Items.Select(t => t.Name));

        var desc = await acct.Client.GetFromJsonAsync<Page>(
            $"/api/v1/documents?folderId={folderId}&sort=name&order=desc&limit=100");
        Assert.Equal(new[] { "Zebra", "Mango", "apple" }, desc!.Items.Select(t => t.Name));
    }

    [Fact]
    public async Task Sorting_by_creation_time_runs_both_ways()
    {
        var acct = await _f.RegisterAsync();
        var folderId = await acct.Client.CreateFolderAsync("Chronological");
        await acct.Client.CreateDocAsync("First", folderId);
        await acct.Client.CreateDocAsync("Second", folderId);
        await acct.Client.CreateDocAsync("Third", folderId);

        var asc = await acct.Client.GetFromJsonAsync<Page>(
            $"/api/v1/documents?folderId={folderId}&sort=created&order=asc&limit=100");
        Assert.Equal(new[] { "First", "Second", "Third" }, asc!.Items.Select(t => t.Name));

        var desc = await acct.Client.GetFromJsonAsync<Page>(
            $"/api/v1/documents?folderId={folderId}&sort=created&order=desc&limit=100");
        Assert.Equal(new[] { "Third", "Second", "First" }, desc!.Items.Select(t => t.Name));
    }

    // The point of the feature: the document touched last comes first, regardless of when it was
    // created. "Stale" is created first and never uploaded to; "Fresh" is created last and uploaded.
    [Fact]
    public async Task Sorting_by_last_updated_follows_the_newest_version_not_the_creation_time()
    {
        var acct = await _f.RegisterAsync();
        var folderId = await acct.Client.CreateFolderAsync("Recency");
        var stale = await acct.Client.CreateDocAsync("Stale", folderId);
        var fresh = await acct.Client.CreateDocAsync("Fresh", folderId);
        await acct.Client.UploadAsync(stale, DocxFixtures.Build("only", "version"));
        await acct.Client.UploadAsync(fresh, DocxFixtures.Build("newest", "version"));

        var page = await acct.Client.GetFromJsonAsync<Page>(
            $"/api/v1/documents?folderId={folderId}&sort=updated&order=desc&limit=100");

        Assert.Equal(new[] { "Fresh", "Stale" }, page!.Items.Select(t => t.Name));
    }

    // A document with no versions has no version time to sort by. It must fall back to its own
    // creation time, not vanish: a NULL cannot take part in a keyset row-value comparison, so an
    // uncoalesced key would silently drop the row from every page.
    [Fact]
    public async Task A_document_with_no_versions_still_appears_when_sorting_by_last_updated()
    {
        var acct = await _f.RegisterAsync();
        var folderId = await acct.Client.CreateFolderAsync("Empties");
        var withVersion = await acct.Client.CreateDocAsync("Has one", folderId);
        await acct.Client.UploadAsync(withVersion, DocxFixtures.Build("a", "version", Guid.NewGuid().ToString("N")));
        var empty = await acct.Client.CreateDocAsync("Has none", folderId);

        var page = await acct.Client.GetFromJsonAsync<Page>(
            $"/api/v1/documents?folderId={folderId}&sort=updated&order=desc&limit=100");

        Assert.Equal(2, page!.Items.Length);
        // Created after the upload, so its creation time is the later of the two.
        Assert.Equal(empty, page.Items[0].Id);
        Assert.Null(page.Items[0].UpdatedAt); // and the tile still says "no versions yet"
    }

    // A sort that only holds within one page is not a sort. Five names read two at a time must come
    // back in one alphabetical sequence, with no row repeated and none dropped.
    [Fact]
    public async Task A_sorted_list_stays_ordered_across_cursor_pages()
    {
        var acct = await _f.RegisterAsync();
        var folderId = await acct.Client.CreateFolderAsync("Paged");
        foreach (var name in new[] { "delta", "Alpha", "echo", "bravo", "Charlie" })
            await acct.Client.CreateDocAsync(name, folderId);

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var url = $"/api/v1/documents?folderId={folderId}&sort=name&order=asc&limit=2"
                + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await acct.Client.GetFromJsonAsync<Page>(url);
            seen.AddRange(page!.Items.Select(t => t.Name));
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Equal(new[] { "Alpha", "bravo", "Charlie", "delta", "echo" }, seen);
    }
```

`DocxFixtures.Build(...)` with a unique paragraph per test, rather than `DocxFixtures.Base()` —
`DocxFixtures.cs`'s own comment explains why: blobs are content-addressed and two tests uploading the
same first-time sha concurrently can race into a 500.

`Page` and `Tile` are the private records already at the top of `DocumentListTests.cs`; no change
needed. Add `using EasyDocs.Api.Tests.Fixtures;` if it is not already imported (it is).

- [ ] **Step 2: Run them to verify they fail**

```bash
dotnet test --filter "FullyQualifiedName~DocumentListTests" 2>&1 | tail -30
```

Expected: the four order assertions FAIL (everything comes back in creation order because `sort` is
still ignored). `A_document_with_no_versions_still_appears` may pass by accident — the ordering
assertion inside it will not.

- [ ] **Step 3: Add the sortable projection and the keyset**

In `DocumentEndpoints.cs`, add near `ListDocuments`:

```csharp
    // Every sort key on one row, so the keyset branches below differ only in which member they read.
    //
    // Updated coalesces to the document's own CreatedAt. Two reasons: a NULL cannot participate in a
    // row-value comparison (`x > NULL` is unknown, so a version-less document would silently
    // disappear from every page), and "last touched when it was created" is true rather than a
    // sentinel. The tile still REPORTS updatedAt as null — DocumentListProjection is unchanged, so a
    // document with no versions still reads "No versions yet".
    //
    // ponytail: the Updated subquery and lower(Name) have no supporting index, so both sorts are a
    // sequential scan plus a sort, and Updated is computed for every filtered row. Fine for an org's
    // library; if a tenant's document count makes the dashboard slow, the upgrade path is a
    // denormalised Document.UpdatedAt maintained on the version write path plus (OrgId, UpdatedAt, Id)
    // and (OrgId, lower(Name), Id) indexes.
    private sealed record SortableDoc(Document Doc, DateTimeOffset Created, DateTimeOffset Updated, string NameKey);

    // Strictly after (or before) the cursor row, with Id breaking ties, and an ORDER BY that matches
    // the comparison exactly — if the two ever disagree, paging skips or repeats rows.
    //
    // Written out per column rather than composed from a key selector: EF cannot invoke a
    // Func<T, TKey> inside a predicate, so the general version means hand-built expression trees plus
    // a separate string path. That is more code than these branches and far harder to read.
    private static IQueryable<SortableDoc> Keyset(
        IQueryable<SortableDoc> rows, byte tag, Pagination.CursorKey? after, bool desc)
    {
        if (after is not null)
        {
            var id = after.Id;
            if (tag == SortName)
            {
                var k = Pagination.AsText(after);
                rows = desc
                    ? rows.Where(r => r.NameKey.CompareTo(k) < 0 || (r.NameKey == k && r.Doc.Id.CompareTo(id) < 0))
                    : rows.Where(r => r.NameKey.CompareTo(k) > 0 || (r.NameKey == k && r.Doc.Id.CompareTo(id) > 0));
            }
            else if (Pagination.AsTime(after) is { } t)
            {
                rows = (tag, desc) switch
                {
                    (SortUpdated, true) => rows.Where(r => r.Updated < t || (r.Updated == t && r.Doc.Id.CompareTo(id) < 0)),
                    (SortUpdated, false) => rows.Where(r => r.Updated > t || (r.Updated == t && r.Doc.Id.CompareTo(id) > 0)),
                    (_, true) => rows.Where(r => r.Created < t || (r.Created == t && r.Doc.Id.CompareTo(id) < 0)),
                    (_, false) => rows.Where(r => r.Created > t || (r.Created == t && r.Doc.Id.CompareTo(id) > 0)),
                };
            }
        }

        return (tag, desc) switch
        {
            (SortName, true) => rows.OrderByDescending(r => r.NameKey).ThenByDescending(r => r.Doc.Id),
            (SortName, false) => rows.OrderBy(r => r.NameKey).ThenBy(r => r.Doc.Id),
            (SortUpdated, true) => rows.OrderByDescending(r => r.Updated).ThenByDescending(r => r.Doc.Id),
            (SortUpdated, false) => rows.OrderBy(r => r.Updated).ThenBy(r => r.Doc.Id),
            (_, true) => rows.OrderByDescending(r => r.Created).ThenByDescending(r => r.Doc.Id),
            (_, false) => rows.OrderBy(r => r.Created).ThenBy(r => r.Doc.Id),
        };
    }
```

- [ ] **Step 4: Replace the paging call in `ListDocuments`**

Swap the single `Pagination.PageAsync(...)` line and the `Results.Ok` that follows it for:

```csharp
        var rows = query.Select(d => new SortableDoc(
            d,
            d.CreatedAt,
            db.Versions.Where(v => v.DocumentId == d.Id).Max(v => (DateTimeOffset?)v.CreatedAt) ?? d.CreatedAt,
            d.Name.ToLower()));

        var desc = Pagination.Descending(order);
        var page = await Pagination.ProbeAsync(
            Keyset(rows, tag, after, desc), limit,
            r => tag == SortName
                ? Pagination.EncodeText(tag, r.NameKey, r.Doc.Id)
                : Pagination.EncodeTime(tag, tag == SortUpdated ? r.Updated : r.Created, r.Doc.Id),
            ctx.RequestAborted);

        return Results.Ok(new
        {
            items = await DocumentListProjection.BuildAsync(
                db, page.Items.Select(r => r.Doc).ToList(), ctx.RequestAborted),
            nextCursor = page.NextCursor,
        });
```

`Document` needs to be in scope — `EasyDocs.Api.Domain` is already imported in this file.

- [ ] **Step 5: Run the document list tests**

```bash
dotnet test --filter "FullyQualifiedName~DocumentListTests" 2>&1 | tail -30
```

Expected: PASS. Two failure modes to expect and how to read them:

- **`InvalidOperationException: could not be translated`** — EF cannot render one of the expressions.
  The likely culprit is `string.CompareTo`. `Guid.CompareTo` is already used in `Pagination.cs` and
  translates fine, so if `NameKey.CompareTo(k)` is the problem, swap it for
  `string.Compare(r.NameKey, k) > 0`, which the relational provider translates to `>`.
- **`Sorting_by_name_is_case_insensitive` returns `Mango, Zebra, apple`** — `d.Name.ToLower()` did not
  reach SQL as `lower(name)` and the comparison happened in the database's collation on the raw
  column. Check the generated SQL by setting `Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=Information`.

- [ ] **Step 6: Run the whole backend suite**

```bash
dotnet test 2>&1 | tail -20
```

Expected: PASS. Particularly `Documents_list_paginates_with_cursor` in `PaginationTests` and every
test in `ContentSearchTests` and `FolderTests`, all of which read this endpoint.

- [ ] **Step 7: Commit**

```bash
git add src/EasyDocs.Api/Documents/DocumentEndpoints.cs tests/EasyDocs.Api.Tests/DocumentListTests.cs
git commit -m "feat(api): sort the document list by name, creation or last update

One projection carries all three keys so the keyset branches differ only in
which member they read. Updated coalesces to the document's own CreatedAt:
a NULL cannot take part in a row-value comparison, so a version-less
document would otherwise vanish from every page. Names compare lower-cased,
or the default collation puts every capital ahead of every lowercase letter."
```

---

## Task 5: The sort control in the dashboard

**Files:**
- Modify: `web/src/routes/Dashboard.tsx`
- Modify: `web/src/index.css` (two rules)

- [ ] **Step 1: Add the URL-backed sort state**

In `Dashboard.tsx`, extend the react-router import and read the params:

```tsx
import { Link, useParams, useSearchParams } from 'react-router'
```

Above the component, the option table — one control, one value, so the pair travels together:

```tsx
// One select rather than a key picker plus a direction toggle: six labelled choices is one control
// and one piece of state, and every label says what you will get rather than naming a column.
const SORTS = [
  ['updated:desc', 'Last updated'],
  ['updated:asc', 'Oldest updated'],
  ['name:asc', 'Name A–Z'],
  ['name:desc', 'Name Z–A'],
  ['created:desc', 'Newest first'],
  ['created:asc', 'Oldest first'],
] as const
```

Inside the component, next to the other state:

```tsx
  // In the URL, not in state: a sorted view survives a reload, is shareable as a link, and comes
  // back when you return from a document. The API's own default is created-asc — the web client opts
  // into last-updated-first the same way History opts into order=desc.
  const [params, setParams] = useSearchParams()
  const sort = params.get('sort') ?? 'updated'
  const order = params.get('order') ?? 'desc'
```

- [ ] **Step 2: Send them, and refetch when they change**

In `load`, after the `q` line:

```tsx
      params.set('sort', sort)
      params.set('order', order)
```

Careful: `load` already has a local `const params = new URLSearchParams()`. That name now collides
with the `useSearchParams` tuple. Rename the local one to `search`:

```tsx
      const search = new URLSearchParams()
      if (folderId) search.set('folderId', folderId)
      if (q) search.set('q', q)
      if (trashed) search.set('trashed', 'true')
      if (cursor) search.set('cursor', cursor)
      search.set('sort', sort)
      search.set('order', order)
      const page = await api.get<Paged<Tile>>(`/api/v1/documents?${search}`)
```

and add them to the dependency array:

```tsx
    [folderId, q, trashed, sort, order],
```

That is the whole refetch mechanism — `load` changing identity re-runs the existing effect with
`cursor: null`, which *replaces* the tile array rather than appending. Exactly what the `q` path
already does.

- [ ] **Step 3: Render the select**

Inside `.docs-tools`, immediately after the closing `</form>` of the search field:

```tsx
              <label className="inline-field sort">
                <span>Sort</span>
                <span className="visually-hidden"> documents</span>
                <select
                  data-testid="sort"
                  value={`${sort}:${order}`}
                  onChange={(e) => {
                    const [s, o] = e.target.value.split(':')
                    const next = new URLSearchParams(params)
                    next.set('sort', s)
                    next.set('order', o)
                    setParams(next)
                  }}
                >
                  {SORTS.map(([value, label]) => (
                    <option key={value} value={value}>
                      {label}
                    </option>
                  ))}
                </select>
              </label>
```

Copied from the existing `URLSearchParams` rather than replaced wholesale, so a future param on this
route is not silently dropped. The label reuses `.inline-field`, the same idiom the tile's "Move to"
field already uses.

The trash view does not render `.docs-tools` at all, so it gets no control — but `load` still sends
the default `sort=updated&order=desc` there. That is deliberate: last-updated-first is a better trash
order than oldest-created-first, and special-casing it would be more code for a worse result.

- [ ] **Step 4: Two CSS rules**

`.inline-field` gets its flex layout from `.tile-actions label`, which does not apply here. Add after
the `.docs-tools .search input` rule (around line 841 of `web/src/index.css`):

```css
/* .inline-field only gets its row from .tile-actions, so the toolbar copy brings its own. */
.docs-tools .sort {
  display: inline-flex;
  align-items: center;
  gap: var(--s2);
}
```

And in the mobile breakpoint, beside the existing `.docs-tools .search input` entry (around line
1617):

```css
  .docs-tools .sort select {
    width: 100%;
  }
```

- [ ] **Step 5: Typecheck and lint**

```bash
npm --prefix web run build && npm --prefix web run lint
```

Expected: both clean. A `params` shadowing error here means Step 2's rename was missed somewhere.

- [ ] **Step 6: Look at it**

```bash
docker compose up -d && npm --prefix web run dev
```

Open `http://localhost:5173`, sign in, and check four things by hand: the toolbar reads
`Sort [Last updated ▾]`; picking `Name A–Z` reorders the tiles and puts `?sort=name&order=asc` in the
address bar; reloading keeps that order; and tabbing from the search field lands on the select and
opens it with the keyboard.

- [ ] **Step 7: Commit**

```bash
git add web/src/routes/Dashboard.tsx web/src/index.css
git commit -m "feat(web): sort the document tiles from the dashboard toolbar

One select with six labelled choices, its state in the URL so a sorted view
survives a reload and is shareable. The default becomes last-updated-first;
the API's own default stays created-asc for existing clients."
```

---

## Task 6: End-to-end coverage

**Files:**
- Modify: `web/e2e/dashboard.spec.ts`

- [ ] **Step 1: Write the failing spec**

Add to `dashboard.spec.ts`. Read the existing specs in the file first for the fixture idioms —
`register`/`signIn` come from `./fixtures`, and `tile(page, name)` is already defined at the top.

```ts
// Sorting has to be server-side and it has to stick: reordering only the tiles already fetched would
// be a lie the moment the list is longer than one page, and a sort that resets when you come back
// from a document is not a sort anyone would use.
test('sorting reorders the tiles and survives a reload', async ({ page, request }) => {
  const account = await register(request)
  await signIn(page, account)

  for (const name of ['zulu-sort', 'alpha-sort', 'mike-sort']) {
    await disclose(newDocumentForm(page))
    await newDocumentForm(page).getByLabel('Document name').fill(name)
    await newDocumentForm(page).getByRole('button', { name: 'Create document' }).click()
    await expect(tile(page, name)).toBeVisible()
  }

  const names = () => page.locator('[data-testid="document-tile"]').evaluateAll(
    (tiles) => tiles.map((t) => t.getAttribute('data-name')),
  )

  await page.getByTestId('sort').selectOption('name:asc')
  await expect(page).toHaveURL(/[?&]sort=name(&|$)/)
  await expect(page).toHaveURL(/[?&]order=asc(&|$)/)
  await expect.poll(names).toEqual(['alpha-sort', 'mike-sort', 'zulu-sort'])

  // The URL is the state, so a hard reload has to come back to the same order.
  await page.reload()
  await expect(page.getByTestId('sort')).toHaveValue('name:asc')
  await expect.poll(names).toEqual(['alpha-sort', 'mike-sort', 'zulu-sort'])

  await page.getByTestId('sort').selectOption('name:desc')
  await expect.poll(names).toEqual(['zulu-sort', 'mike-sort', 'alpha-sort'])
})
```

`register` and `signIn` may not yet be imported in this file — check the import line at the top and
add whichever are missing.

- [ ] **Step 2: Run it against the real stack**

```bash
docker compose up -d
npm --prefix web run e2e -- dashboard.spec.ts -g "sorting reorders"
```

Expected: PASS, since Task 5 shipped the control. If it fails on the URL assertion, `setParams` is
not being called; if it fails on the order, the API is not receiving `sort` — check the network tab
for the actual `/api/v1/documents` query string.

- [ ] **Step 3: Run the whole dashboard spec**

```bash
npm --prefix web run e2e -- dashboard.spec.ts
```

Expected: PASS. The other specs in this file read tiles in the order the API returns them; if one now
fails, it was implicitly relying on creation order and needs its assertion made order-independent —
**do not** change the default sort back to fix it.

- [ ] **Step 4: Commit**

```bash
git add web/e2e/dashboard.spec.ts
git commit -m "test(e2e): cover sorting the document tiles and its URL state"
```

---

## Task 7: Document the new parameters

**Files:**
- Modify: `docs-site/` user guide page for the dashboard — find it first:
  `grep -rl "Search names" docs-site/ docs/`
- Modify: the API reference if it lists query parameters by hand rather than generating them from
  OpenAPI — check with `grep -rn "trashed" docs-site/ docs/ --include=*.md`

- [ ] **Step 1: Find out whether either needs a change**

```bash
grep -rn "Search names\|trashed=true\|folderId=" docs-site docs --include=*.md --include=*.mdx | grep -v superpowers
```

If the API docs are generated from the OpenAPI document, `sort` and `order` appear without any edit
and only the user guide needs a sentence. If nothing turns up at all, skip this task and say so.

- [ ] **Step 2: Add one sentence to the user guide's dashboard section**

Naming the control and the fact that the choice stays in the address bar, in the voice of the
surrounding page. No table of the six options — the select is self-describing.

- [ ] **Step 3: Commit**

```bash
git commit -am "docs: mention the dashboard sort control in the user guide"
```

---

## Done When

- [ ] `dotnet test` passes with no Docker-unrelated failures
- [ ] `npm --prefix web run build` and `npm --prefix web run lint` are clean
- [ ] `npm --prefix web run e2e -- dashboard.spec.ts` passes against `docker compose up -d`
- [ ] Opening the dashboard shows the most recently updated document first
- [ ] `GET /api/v1/documents` with no `sort` returns the same order it did before this branch —
      verify by hand: `curl` it on `main` and on this branch with the same seed data
