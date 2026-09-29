# Reading a version's text — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** An MCP-visible `GET /api/v1/versions/{vid}/text` that returns one version's plain text, so
an agent can read what a document says instead of only its metadata.

**Architecture:** `DocxText.Extract` grows a return shape that distinguishes "not a docx" from "a
docx with nothing in it" (a bare `""` cannot, and `BlobMime.Sniff` defaults to docx so sniffing
cannot gate it either). One endpoint in the existing `/api/v1/versions` group mirrors `Download`'s
authorization and turns `null` text into a 415 naming the real mime. The MCP server needs one
`NAMES` entry — the `Documents` tag and existing route maps do the rest.

**Tech Stack:** C# minimal APIs, EF Core, xUnit + Testcontainers; Python FastMCP + pytest.

**Spec:** `docs/superpowers/specs/2026-09-10-mcp-content-tools-design.md`

**Commands:** `dotnet test` (needs Docker) · `pytest packages/mcp -q` (needs
`pip install "fastmcp>=3.4,<4" pytest`) · snapshot regen:
`UPDATE_OPENAPI_SNAPSHOT=1 dotnet test --filter Openapi_snapshot_in_docs_site_matches`

---

## Task 1: `DocxText.Extract` says why it produced nothing

**Files:**
- Modify: `src/EasyDocs.Api/Documents/DocxText.cs`
- Modify: `src/EasyDocs.Api/Documents/TextIndexWorker.cs:45`
- Test: `tests/EasyDocs.Api.Tests/ContentSearchTests.cs` (class `DocxTextTests`)

- [ ] **Step 1: Rewrite `DocxTextTests` against the new shape**

```csharp
public class DocxTextTests
{
    [Fact]
    public void Extracts_paragraph_text_with_boundaries()
    {
        var text = DocxText.Extract(new MemoryStream(DocxFixtures.Build("Alpha", "Bravo", "Charlie"))).Text;
        Assert.Contains("Alpha", text);
        Assert.Contains("Bravo", text);
        Assert.Contains("Charlie", text);
        Assert.DoesNotContain("AlphaBravo", text); // paragraph boundary must become whitespace
    }

    // null, not "": the endpoint answers 415 for these and 200 for a blank docx, and only the
    // extractor can tell them apart. Sniffing cannot — BlobMime.Sniff defaults to docx.
    [Theory]
    [InlineData("%PDF-1.7 not a zip at all")]
    [InlineData("just plain text")]
    public void Non_docx_bytes_extract_to_null(string content)
        => Assert.Null(DocxText.Extract(new MemoryStream(Encoding.UTF8.GetBytes(content))).Text);

    [Fact]
    public void A_docx_with_no_text_extracts_to_empty_not_null()
        => Assert.Equal("", DocxText.Extract(new MemoryStream(DocxFixtures.Build())).Text);

    [Fact]
    public void Truncation_is_reported_by_the_extractor()
    {
        // The caller cannot compute this: Extract trims AFTER truncating, and the character at the
        // cap is usually the paragraph boundary, so a length check reads false on a truncated doc.
        var big = DocxText.Extract(new MemoryStream(DocxFixtures.Build(new string('x', DocxText.MaxChars + 1000))));
        Assert.True(big.Truncated);
        Assert.True(big.Text!.Length <= DocxText.MaxChars);

        Assert.False(DocxText.Extract(new MemoryStream(DocxFixtures.Base())).Truncated);
    }
}
```

- [ ] **Step 2: Run them and watch them fail to compile**

Run: `dotnet test --filter DocxTextTests`
Expected: build failure — `Text` and `Truncated` do not exist on `string`.

- [ ] **Step 3: Change the return shape**

In `DocxText.cs`, above `MaxChars`:

```csharp
    /// <summary>
    /// What the bytes turned out to be. <c>Text is null</c> means they are not a docx at all (not a
    /// zip, or a zip with no <c>word/document.xml</c>); <c>Text is ""</c> means a real docx with no
    /// text in it. The index treats both as nothing to index; a caller serving a reader must not.
    /// </summary>
    public readonly record struct Extraction(string? Text, bool Truncated);
```

Then `Extract` returns it: signature `public static Extraction Extract(Stream docx)`, both early
returns become `new Extraction(null, false)`, and the tail becomes

```csharp
            // The loop's own exit condition, read before Trim eats the boundary character.
            var truncated = sb.Length >= MaxChars;
            return new Extraction(truncated ? sb.ToString(0, MaxChars).Trim() : sb.ToString().Trim(), truncated);
```

- [ ] **Step 4: Fix the one production call site**

`TextIndexWorker.cs:45` → `text = DocxText.Extract(seekable).Text ?? "";`
(`?? ""` preserves today's behaviour exactly: a non-docx head clears the index row.)

- [ ] **Step 5: Run the tests**

Run: `dotnet test --filter "DocxTextTests|ContentSearch"`
Expected: PASS — including the end-to-end content search, which proves the index still works.

- [ ] **Step 6: Commit**

```bash
git add src/EasyDocs.Api/Documents/DocxText.cs src/EasyDocs.Api/Documents/TextIndexWorker.cs \
        tests/EasyDocs.Api.Tests/ContentSearchTests.cs
git commit -m "refactor(text): Extract reports why it produced nothing"
```

---

## Task 2: paragraph boundaries become newlines

**Files:**
- Modify: `src/EasyDocs.Api/Documents/DocxText.cs:45`
- Test: `tests/EasyDocs.Api.Tests/ContentSearchTests.cs`

- [ ] **Step 1: Tighten the boundary assertion**

Add to `Extracts_paragraph_text_with_boundaries`:

```csharp
        Assert.Equal("Alpha\nBravo\nCharlie", text); // structure, not one run-on line
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test --filter Extracts_paragraph_text_with_boundaries`
Expected: FAIL — actual is `"Alpha Bravo Charlie"`.

- [ ] **Step 3: One character**

`DocxText.cs:45`: `sb.Append(' ')` → `sb.Append('\n')`.

Leave the comment above it, but extend it: a tsvector treats both as `blank`, so this is free for
search (`SearchVector` is a stored generated column over `Text`); a reader gets paragraphs.

- [ ] **Step 4: Run the search tests too — this is the risk**

Run: `dotnet test --filter "DocxTextTests|ContentSearch"`
Expected: PASS. If content search fails here, stop: the newline reached the tsvector differently
than the spec predicted and the change must be reverted rather than worked around.

- [ ] **Step 5: Commit**

```bash
git add src/EasyDocs.Api/Documents/DocxText.cs tests/EasyDocs.Api.Tests/ContentSearchTests.cs
git commit -m "feat(text): paragraph boundaries extract as newlines"
```

---

## Task 3: the endpoint

**Files:**
- Modify: `src/EasyDocs.Api/Documents/DocumentEndpoints.cs` (register at `:43`, handler after `Download`)
- Test: `tests/EasyDocs.Api.Tests/DownloadTests.cs` (its `AuthedClientAsync` / `CreateDocAsync` /
  `UploadAsync` helpers are exactly what these need — no new file)

- [ ] **Step 1: Write both failing tests**

```csharp
    [Fact]
    public async Task Text_returns_the_versions_plain_text()
    {
        var (c, _) = await AuthedClientAsync();
        var docId = await CreateDocAsync(c, "Master Lease");
        var v = await UploadAsync(c, docId, DocxFixtures.Build("Alpha", "Bravo"));

        var resp = await c.GetAsync($"/api/v1/versions/{v.VersionId}/text");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Alpha\nBravo", body.GetProperty("text").GetString());
        Assert.False(body.GetProperty("truncated").GetBoolean());
        Assert.Equal(1, body.GetProperty("revision").GetInt32());
    }

    // The silent-blank case the 415 exists for: these bytes SNIFF as docx (Sniff defaults to it),
    // so only the extractor knows they are not one.
    [Fact]
    public async Task Text_refuses_a_non_docx_version_and_names_the_mime()
    {
        var (c, _) = await AuthedClientAsync();
        var docId = await CreateDocAsync(c, "Laundry Agreement.pdf");
        var v = await UploadAsync(c, docId, System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\nlease body\n%%EOF"));

        var resp = await c.GetAsync($"/api/v1/versions/{v.VersionId}/text");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, resp.StatusCode);
        Assert.Contains("application/pdf", await resp.Content.ReadAsStringAsync());
    }
```

- [ ] **Step 2: Run them and watch them 404**

Run: `dotnet test --filter "Text_returns_the_versions_plain_text|Text_refuses_a_non_docx"`
Expected: FAIL — 404, the route does not exist.

- [ ] **Step 3: Register the route**

`DocumentEndpoints.cs`, next to the other two `/api/v1/versions` reads:

```csharp
        v.MapGet("/{vid:guid}/text", Text);
```

- [ ] **Step 4: Write the handler** (directly after `Download`)

```csharp
    // The model-readable twin of Download (spec 2026-09-10): one version's plain text. Viewer+, same
    // chokepoint. Extracted per call rather than served from the DocumentTexts index, because that
    // row is the main-branch head at last indexing time — it cannot answer for a historical version
    // or a concurrent branch, and would be silently stale for the poll interval.
    private static async Task<IResult> Text(Guid vid, HttpContext ctx, EasyDocsDbContext db, IBlobStore blobs)
    {
        var version = await db.Versions.FirstOrDefaultAsync(x => x.Id == vid, ctx.RequestAborted);
        if (version is null) return Problem.Of(404, "Not found", "Version not found.");

        var (_, failure) = await AuthorizeAsync(db, ctx, version.DocumentId, requireEdit: false);
        if (failure is not null) return failure;

        // ZipArchive needs a seekable stream and the S3 backend's is not — TextIndexWorker spools for
        // the same reason.
        using var seekable = new MemoryStream();
        await using (var stream = await blobs.OpenReadAsync(version.BlobSha256, ctx.RequestAborted))
            await stream.CopyToAsync(seekable, ctx.RequestAborted);
        seekable.Position = 0;

        var extraction = DocxText.Extract(seekable);
        if (extraction.Text is null)
        {
            // Sniffing cannot GATE this (Sniff defaults to docx, so arbitrary bytes would pass), but
            // once the extractor has established these bytes are not a docx it is the right thing to
            // NAME them with.
            var (mime, _) = await BlobMime.SniffAsync(blobs, version.BlobSha256, ctx.RequestAborted);
            return Problem.Of(415, "Unsupported media type",
                $"This version is {mime}; text extraction supports .docx only.");
        }

        return Results.Ok(new
        {
            versionId = version.Id,
            major = version.Major, minor = version.Minor, revision = version.Revision,
            text = extraction.Text,
            truncated = extraction.Truncated,
        });
    }
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test --filter DownloadTests`
Expected: PASS, all of them — the existing download tests must not move.

- [ ] **Step 6: Commit**

```bash
git add src/EasyDocs.Api/Documents/DocumentEndpoints.cs tests/EasyDocs.Api.Tests/DownloadTests.cs
git commit -m "feat(api): GET /api/v1/versions/{vid}/text"
```

---

## Task 4: pin the authorization where every other read is pinned

**Files:**
- Modify: `tests/EasyDocs.Api.Tests/Conformance/E12_Security.cs`

- [ ] **Step 1: Add the path to all three loops**

In `Reads_are_allowed_for_every_member_and_denied_to_everyone_else`:

1. member loop (after the `download` line):
   `AssertOk(await api.Http.GetAsync($"/api/v1/versions/{c.VersionId}/text"), $"{name} GET version text");`
2. stranger, next to the existing version-path assertion:
   `AssertStatus(HttpStatusCode.Forbidden, await c.Stranger.Http.GetAsync($"/api/v1/versions/{c.VersionId}/text"), "stranger GET version text");`
3. outsider array: add `$"/api/v1/versions/{c.VersionId}/text"`.

- [ ] **Step 2: Run it**

Run: `dotnet test --filter E12`
Expected: PASS — the fixture uploads `DocxFixtures.Base()`, a real docx, so members get 200.

- [ ] **Step 3: Commit**

```bash
git add tests/EasyDocs.Api.Tests/Conformance/E12_Security.cs
git commit -m "test(security): pin /versions/{vid}/text in the read matrix"
```

---

## Task 5: regenerate the OpenAPI snapshot

**Files:**
- Modify: `docs-site/docs/api/openapi/v1.json` (generated — never hand-edited)

- [ ] **Step 1: Confirm it currently fails**

Run: `dotnet test --filter Openapi_snapshot_in_docs_site_matches`
Expected: FAIL — the served document now has a path the snapshot lacks.

- [ ] **Step 2: Regenerate and re-run**

```bash
UPDATE_OPENAPI_SNAPSHOT=1 dotnet test --filter Openapi_snapshot_in_docs_site_matches
dotnet test --filter Openapi
```
Expected: PASS. Check `git diff` shows only the new `/api/v1/versions/{vid}/text` path.

- [ ] **Step 3: Commit**

```bash
git add docs-site/docs/api/openapi/v1.json
git commit -m "docs(api): regenerate the OpenAPI snapshot"
```

---

## Task 6: the MCP tool

**Files:**
- Modify: `packages/mcp/easydocs_mcp.py` (`NAMES`, and the stale comment at `:20`)
- Test: `packages/mcp/test_easydocs_mcp.py:17`

- [ ] **Step 1: Add the expected tool name to the exact-set assertion**

`EXPECTED`: add `"get_version_text"`.

- [ ] **Step 2: Run it and watch it fail**

Run: `pytest packages/mcp -q`
Expected: FAIL — `missing: {'get_version_text'}`.

- [ ] **Step 3: Add the one line**

In `NAMES`, after the `get_version` entry:

```python
    "GET /api/v1/versions/{vid}/text": "get_version_text",
```

And `:20`'s comment: "The seventeen tools." → "The eighteen tools." No `RouteMap` change — the
endpoint carries the `Documents` tag, and the download exclusion is anchored `/download$`.

- [ ] **Step 4: Run it**

Run: `pytest packages/mcp -q`
Expected: PASS, both tests — `test_every_tool_is_a_get` included.

- [ ] **Step 5: Commit**

```bash
git add packages/mcp/easydocs_mcp.py packages/mcp/test_easydocs_mcp.py
git commit -m "feat(mcp): expose get_version_text"
```

---

## Task 7: the prose

**Files:**
- Modify: `packages/mcp/README.md` (tool table)
- Modify: `docs-site/docs/automation-recipes.md:257`
- Modify: `CHANGELOG.md` (`## [Unreleased]` → `### Added`)

- [ ] **Step 1: Table row, after `get_version`**

```markdown
| `get_version_text` | the version's text, for reading or summarizing (`.docx` only — 415 names anything else) |
```

- [ ] **Step 2: Count**

`automation-recipes.md:257`: "seventeen tools" → "eighteen tools".
**Leave `CHANGELOG.md`'s existing "seventeen tools" alone** — it is a shipped-release note, not a
live count.

- [ ] **Step 3: Changelog entry under `## [Unreleased]` / `### Added`**

```markdown
- **Read a version's text over the API and MCP** — `GET /api/v1/versions/{vid}/text` returns one
  version's plain text (`.docx`; anything else answers 415 naming its real type), exposed as the
  `get_version_text` MCP tool. Paragraph boundaries now extract as newlines, so a long agreement
  reads as paragraphs instead of one line.
```

- [ ] **Step 4: Commit**

```bash
git add packages/mcp/README.md docs-site/docs/automation-recipes.md CHANGELOG.md
git commit -m "docs: get_version_text in the tool table, count and changelog"
```

---

## Task 8: full verification before the PR

- [ ] **Step 1:** `dotnet test` — the whole backend suite, not a filter.
- [ ] **Step 2:** `pytest packages/mcp -q`.
- [ ] **Step 3:** `git diff main --stat` — expect ~9 files and no stray `prod-*.sh` or
  `docs/postmortems/` (both are untracked work from another branch and must stay out).
- [ ] **Step 4:** Open the PR, then run the pre-merge gate: a fresh agent using
  `/ponytail:ponytail`, ending in a literal `MERGE` / `DO NOT MERGE` line. If it blocks, fix and
  re-gate rather than merging.
