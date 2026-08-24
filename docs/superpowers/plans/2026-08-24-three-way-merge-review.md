# Three-way merge review — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Route the History **Merge** button through a read-only three-way review screen that shows the fork point, both sides' changes since it, and a best-effort overlap hint — without changing what a merge commits.

**Architecture:** A new `GET /api/v1/documents/{id}/merges/preview` endpoint returns the base version, both sides' summaries, and overlapping base paragraphs. A new `/documents/:id/merge` SPA route renders that plus two redlines fetched from the **existing** `/compare` endpoint. `POST /merges` is untouched; the Merge button simply moves to the review screen.

**Tech Stack:** ASP.NET Core 10 minimal APIs · Clippit `WmlComparer` · EF Core / PostgreSQL · React 19 + react-router · xUnit · Playwright

**Spec:** `docs/superpowers/specs/2026-08-24-three-way-merge-review-design.md`

---

## Read this before Task 1

**The one deliberate deviation from the spec.** The spec says `WmlComparerMergeService.cs` is not
touched. Task 1 performs a **behaviour-preserving extraction** on it: the ~15 lines that decide which
of `left`/`right` is the incoming side move to a shared `MergeSides` helper that both the merge and
its preview call.

This is worth the deviation because the alternative is duplicating that decision in the preview, and a
preview that picks a different side than the merge would **lie to the user** — the exact failure this
whole feature exists to prevent. `MergeTests.cs` and `PushMergeTests.cs` are the proof it is
behaviour-preserving: they must stay green **without edits**. If either needs an edit, stop — the
extraction changed behaviour and the approach needs revisiting.

**Files touched overall:**

| File | Action | Responsibility |
| --- | --- | --- |
| `src/EasyDocs.Api/Merging/MergeSides.cs` | Create | Which version is incoming, which is main head |
| `src/EasyDocs.Api/Merging/ThreeWayOverlap.cs` | Create | **Pure**: two compared docs → overlapping base paragraphs |
| `src/EasyDocs.Api/Merging/MergePreviewService.cs` | Create | Orchestrates base/main/incoming + three compares |
| `src/EasyDocs.Api/Merging/WmlComparerMergeService.cs` | Modify | Call `MergeSides` instead of inline logic |
| `src/EasyDocs.Api/Merging/MergeEndpoints.cs` | Modify | +1 `GET` route |
| `src/EasyDocs.Api/Program.cs` | Modify | Register `MergePreviewService` |
| `tests/.../ThreeWayOverlapTests.cs` | Create | Pure unit tests, no host, no DB |
| `tests/.../MergePreviewTests.cs` | Create | Endpoint tests |
| `web/src/api.ts` | Modify | `MergePreview` types |
| `web/src/routes/MergeReview.tsx` | Create | The review screen |
| `web/src/App.tsx` | Modify | +1 route |
| `web/src/routes/History.tsx` | Modify | Merge button → `<Link>` |
| `web/e2e/fixtures.ts` | Modify | Host `raceConcurrentBranch` (shared by two specs) |
| `web/e2e/console.spec.ts` | Modify | Existing merge test now goes via the review |
| `web/e2e/merge-review.spec.ts` | Create | The new screen's coverage |
| `CHANGELOG.md` | Modify | `[Unreleased] → Added` |

**Run the whole C# suite with:** `dotnet test tests/EasyDocs.Api.Tests`
(Testcontainers starts PostgreSQL; Docker must be running.)
**Run one test:** `dotnet test tests/EasyDocs.Api.Tests --filter "FullyQualifiedName~ThreeWayOverlapTests"`
**Run e2e:** `npm --prefix web run e2e -- merge-review.spec.ts` (needs the API on :8080)

---

## Task 1: Extract the merge side-resolution

**Files:**
- Create: `src/EasyDocs.Api/Merging/MergeSides.cs`
- Modify: `src/EasyDocs.Api/Merging/WmlComparerMergeService.cs:28-52`
- Guard: `tests/EasyDocs.Api.Tests/MergeTests.cs`, `tests/EasyDocs.Api.Tests/PushMergeTests.cs` (**no edits**)

- [ ] **Step 1: Confirm the guard suite is green before touching anything**

Run: `dotnet test tests/EasyDocs.Api.Tests --filter "FullyQualifiedName~MergeTests|FullyQualifiedName~PushMergeTests"`
Expected: PASS. If it is already red, stop and report — you cannot prove a refactor safe against a red suite.

- [ ] **Step 2: Create the shared resolver**

```csharp
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Merging;

// Which of the two version ids is "somebody else's work to bring onto main", and what main's head is
// right now. Extracted from WmlComparerMergeService so the merge PREVIEW resolves the identical sides:
// a preview that described a different merge than the button performs would be worse than no preview.
internal static class MergeSides
{
    internal record Sides(
        DocumentVersion Incoming, Branch IncomingBranch,
        DocumentVersion MainHead, Branch MainBranch);

    // Two branch kinds qualify as incoming: a Concurrent branch from a stale-base save (M1, E4) and an
    // IncomingPush branch materialised from a copy (M4, E9). Both are treated identically.
    internal static bool IsIncoming(Branch b) => b.Kind is BranchKind.Concurrent or BranchKind.IncomingPush;

    // null when the merge cannot be attempted at all: a version id that is not this document's, neither
    // side on an incoming branch, or a document with no main head yet.
    internal static async Task<Sides?> ResolveAsync(
        EasyDocsDbContext db, Guid documentId, Guid leftVersionId, Guid rightVersionId, CancellationToken ct)
    {
        var left = await db.Versions.FirstOrDefaultAsync(v => v.Id == leftVersionId && v.DocumentId == documentId, ct);
        var right = await db.Versions.FirstOrDefaultAsync(v => v.Id == rightVersionId && v.DocumentId == documentId, ct);
        if (left is null || right is null) return null;

        var leftBranch = await db.Branches.FirstAsync(b => b.Id == left.BranchId, ct);
        var rightBranch = await db.Branches.FirstAsync(b => b.Id == right.BranchId, ct);

        var (incoming, incomingBranch) = IsIncoming(rightBranch) ? (right, rightBranch)
            : IsIncoming(leftBranch) ? (left, leftBranch)
            : (null, null!);
        if (incoming is null) return null;

        var mainBranch = await db.Branches.FirstAsync(b => b.DocumentId == documentId && b.Ordinal == 0, ct);
        var mainHead = await db.Versions.Where(v => v.BranchId == mainBranch.Id)
            .OrderByDescending(v => v.SeqInBranch).FirstOrDefaultAsync(ct);
        if (mainHead is null) return null;

        return new Sides(incoming, incomingBranch, mainHead, mainBranch);
    }
}
```

- [ ] **Step 3: Rewrite the head of `MergeAsync` to use it**

Replace everything in `WmlComparerMergeService.MergeAsync` from the `var left = …` line through the
`if (mainHead is null) return new MergeResult(false, null);` line with:

```csharp
        // Sides live in MergeSides so the preview endpoint resolves them identically (spec:
        // 2026-08-24-three-way-merge-review-design.md).
        //
        // ponytail: an incoming_push branch carries the fork point in RootVersionId (spec §8), and
        // merge-into-main (§5.3 [D]) still does not read it — it compares the current main head against
        // the incoming head, so the common ancestor is provenance, not a merge input. The three-way
        // REVIEW surfaces that ancestor to the user without changing this. Ceiling unchanged: a true
        // three-way fuse of both authors over the ancestor is the deferred v1.1 enhancement in §5.3.
        var sides = await MergeSides.ResolveAsync(db, documentId, leftVersionId, rightVersionId, ct);
        if (sides is null) return new MergeResult(false, null);
        var (incoming, incomingBranch, mainHead, mainBranch) = sides;
```

Leave the rest of the method (author lookup, the guarded `Compare`, `PutAsync`, `CommitSaveAsync`,
`MergedIntoVersionId`, `bus.Publish`) exactly as it is. Delete the now-unused private `IsIncoming`.

- [ ] **Step 4: Prove behaviour is unchanged**

Run: `dotnet test tests/EasyDocs.Api.Tests --filter "FullyQualifiedName~MergeTests|FullyQualifiedName~PushMergeTests"`
Expected: PASS, **with no edits to either test file**. If a test needed changing, revert and report.

- [ ] **Step 5: Commit**

```bash
git add src/EasyDocs.Api/Merging/MergeSides.cs src/EasyDocs.Api/Merging/WmlComparerMergeService.cs
git commit -s -m "refactor(merging): extract merge side-resolution into MergeSides

The merge preview must resolve the same incoming/main pair the merge
itself does; a preview describing a different merge would be worse than
none. Behaviour-preserving -- MergeTests and PushMergeTests pass
unedited."
```

---

## Task 2: The overlap walker (pure, TDD)

**Files:**
- Create: `src/EasyDocs.Api/Merging/ThreeWayOverlap.cs`
- Test: `tests/EasyDocs.Api.Tests/ThreeWayOverlapTests.cs`

This is the only genuinely new algorithm in the feature and the only part that can be *wrong*. It has
no DB, no blob store and no host, so it is tested directly.

- [ ] **Step 1: Write the failing tests**

```csharp
using Clippit;
using EasyDocs.Api.Merging;
using EasyDocs.Api.Tests.Fixtures;

public class ThreeWayOverlapTests
{
    // Compare two fixture documents the same way the product does, so the walker is exercised against
    // real WmlComparer output rather than hand-written XML that might not match what Clippit emits.
    private static WmlDocument Compare(byte[] from, byte[] to) =>
        WmlComparer.Compare(new WmlDocument("f.docx", from), new WmlDocument("t.docx", to),
            new WmlComparerSettings());

    [Fact]
    public void Reports_a_paragraph_both_sides_changed()
    {
        var b = DocxFixtures.Build("Alpha", "Bravo", "Charlie");
        var main = Compare(b, DocxFixtures.Build("Alpha", "Bravo MAIN", "Charlie"));
        var incoming = Compare(b, DocxFixtures.Build("Alpha", "Bravo BRANCH", "Charlie"));

        var overlaps = ThreeWayOverlap.Find(main, incoming);

        Assert.Single(overlaps);
        Assert.Equal(1, overlaps[0].Ordinal);          // zero-based: Alpha=0, Bravo=1
        Assert.Contains("Bravo", overlaps[0].Text);    // labelled from BASE text, not either edit
    }

    [Fact]
    public void Ignores_a_paragraph_only_one_side_changed()
    {
        var b = DocxFixtures.Build("Alpha", "Bravo", "Charlie");
        var main = Compare(b, DocxFixtures.Build("Alpha MAIN", "Bravo", "Charlie"));
        var incoming = Compare(b, DocxFixtures.Build("Alpha", "Bravo", "Charlie BRANCH"));

        Assert.Empty(ThreeWayOverlap.Find(main, incoming));
    }

    // THE regression this design exists to prevent. Main inserts a paragraph ABOVE the shared edit, so
    // naive index alignment would shift main's ordinals by one and miss the overlap entirely.
    [Fact]
    public void An_insertion_on_one_side_does_not_shift_the_other_sides_ordinals()
    {
        var b = DocxFixtures.Build("Alpha", "Bravo", "Charlie");
        var main = Compare(b, DocxFixtures.Build("Alpha", "INSERTED", "Bravo MAIN", "Charlie"));
        var incoming = Compare(b, DocxFixtures.Build("Alpha", "Bravo BRANCH", "Charlie"));

        var overlaps = ThreeWayOverlap.Find(main, incoming);

        Assert.Single(overlaps);
        Assert.Equal(1, overlaps[0].Ordinal);
        Assert.Contains("Bravo", overlaps[0].Text);
    }

    // The case that rejected text-matching as the anchor: real leases repeat "Intentionally omitted."
    // many times, and matching on text would call every repetition an overlap.
    [Fact]
    public void Repeated_identical_paragraphs_do_not_produce_false_positives()
    {
        var b = DocxFixtures.Build("Intentionally omitted.", "Bravo", "Intentionally omitted.");
        var main = Compare(b, DocxFixtures.Build("Intentionally omitted. MAIN", "Bravo", "Intentionally omitted."));
        var incoming = Compare(b, DocxFixtures.Build("Intentionally omitted.", "Bravo", "Intentionally omitted. BRANCH"));

        // Ordinal 0 and ordinal 2 are different paragraphs that happen to read the same. One side
        // touched each. Neither is an overlap.
        Assert.Empty(ThreeWayOverlap.Find(main, incoming));
    }

    [Fact]
    public void A_paragraph_deleted_by_both_sides_is_an_overlap()
    {
        var b = DocxFixtures.Build("Alpha", "Bravo", "Charlie");
        var main = Compare(b, DocxFixtures.Build("Alpha", "Charlie"));
        var incoming = Compare(b, DocxFixtures.Build("Alpha", "Charlie"));

        var overlaps = ThreeWayOverlap.Find(main, incoming);

        Assert.Single(overlaps);
        Assert.Contains("Bravo", overlaps[0].Text);   // labelled from base content it no longer has
    }

    [Fact]
    public void Long_paragraphs_are_truncated_with_an_ellipsis()
    {
        var long1 = new string('x', 80) + " TAIL";
        var b = DocxFixtures.Build(long1);
        var main = Compare(b, DocxFixtures.Build(long1 + " MAIN"));
        var incoming = Compare(b, DocxFixtures.Build(long1 + " BRANCH"));

        var text = ThreeWayOverlap.Find(main, incoming)[0].Text;

        Assert.EndsWith("…", text);
        Assert.DoesNotContain("TAIL", text);
        Assert.True(text.Length <= 61);   // 60 chars + the ellipsis
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/EasyDocs.Api.Tests --filter "FullyQualifiedName~ThreeWayOverlapTests"`
Expected: **build failure** — `ThreeWayOverlap` does not exist. That is the correct red for a new type.

- [ ] **Step 3: Implement the walker**

```csharp
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Clippit;

namespace EasyDocs.Api.Merging;

/// <summary>
/// Pure: given two documents each compared against the SAME base, report the base paragraphs that both
/// comparisons changed. No database, no blob store, no host — the fiddly part of the three-way review
/// is testable on its own (spec: 2026-08-24-three-way-merge-review-design.md).
/// </summary>
public static class ThreeWayOverlap
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public record Paragraph(int Ordinal, string Text);

    public static IReadOnlyList<Paragraph> Find(WmlDocument comparedMain, WmlDocument comparedIncoming)
    {
        var main = Touched(comparedMain);
        var incoming = Touched(comparedIncoming);

        // Labels are taken from either side: both reconstruct the SAME base text for a given ordinal,
        // because both were compared against the same base.
        return main.Keys.Where(incoming.ContainsKey).Order()
            .Select(o => new Paragraph(o, main[o]))
            .ToList();
    }

    // base-paragraph ordinal -> its label, for the base paragraphs this comparison changed.
    private static Dictionary<int, string> Touched(WmlDocument compared)
    {
        using var zip = new ZipArchive(new MemoryStream(compared.DocumentByteArray), ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml")
            ?? throw new InvalidOperationException("compared docx has no word/document.xml");
        XDocument doc;
        using (var s = entry.Open()) doc = XDocument.Load(s);

        var touched = new Dictionary<int, string>();
        var ordinal = 0;
        foreach (var p in doc.Descendants(W + "p"))
        {
            var hasBaseContent = p.Descendants(W + "t").Any(t => !t.Ancestors(W + "ins").Any())
                              || p.Descendants(W + "delText").Any();
            var hasInsertion = p.Descendants(W + "ins").Any();

            // A paragraph that is ALL insertion never existed in the base, so numbering it would shift
            // every later ordinal out of step with the other comparison — which is exactly the bug this
            // anchor exists to avoid. Skipping them is what makes `ordinal` the BASE document's own
            // paragraph numbering, identical across both comparisons by construction.
            //
            // The test is "has insertions AND no base content" rather than "has no base content", so a
            // genuinely EMPTY base paragraph (common in real documents) is still counted.
            if (hasInsertion && !hasBaseContent) continue;

            if (hasInsertion || p.Descendants(W + "del").Any())
                touched[ordinal] = Label(BaseText(p));
            ordinal++;
        }
        return touched;
    }

    // The paragraph as it stood in the base: text that survived (w:t outside w:ins) plus text that was
    // removed (w:delText), in document order. Insertions are excluded — they were never in the base, so
    // including them would label a paragraph with words one author has just added.
    private static string BaseText(XElement p)
    {
        var sb = new StringBuilder();
        foreach (var t in p.Descendants().Where(e => e.Name == W + "t" || e.Name == W + "delText"))
            if (t.Name == W + "delText" || !t.Ancestors(W + "ins").Any())
                sb.Append(t.Value);
        return sb.ToString().Trim();
    }

    private const int LabelLength = 60;

    private static string Label(string text) =>
        text.Length <= LabelLength ? text : text[..LabelLength].TrimEnd() + "…";
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/EasyDocs.Api.Tests --filter "FullyQualifiedName~ThreeWayOverlapTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Add the ceiling comment**

Append to the `<summary>` block on the class:

```csharp
/// <remarks>
/// ponytail: a paragraph SPLIT on one side (someone pressing Enter mid-paragraph) shifts that side's
/// ordinals by one from the split down, so overlaps after it can be missed or attributed to the
/// neighbouring paragraph. This is why the result ships worded as a hint and never blocks a merge.
/// Upgrade path: anchor on w14:paraId where the attribute is present, falling back to the ordinal.
/// </remarks>
```

- [ ] **Step 6: Commit**

```bash
git add src/EasyDocs.Api/Merging/ThreeWayOverlap.cs tests/EasyDocs.Api.Tests/ThreeWayOverlapTests.cs
git commit -s -m "feat(merging): find base paragraphs both sides of a merge changed

Anchored on base-paragraph ordinal -- counting only paragraphs that are
not wholly inserted reconstructs the base document's own numbering,
identically in both comparisons. Text matching was rejected: real leases
repeat 'Intentionally omitted.' and every repetition would be a false
positive."
```

---

## Task 3: The preview service

**Files:**
- Create: `src/EasyDocs.Api/Merging/MergePreviewService.cs`
- Modify: `src/EasyDocs.Api/Program.cs` (register it beside `WmlComparerMergeService`)

- [ ] **Step 1: Write the service**

```csharp
using Clippit;
using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Storage;
using EasyDocs.Api.Versioning;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Merging;

// The read half of merging (spec: 2026-08-24-three-way-merge-review-design.md). Commits nothing,
// writes no audit row, and every comparison is guarded — the same discipline WmlComparerDiffService
// enforces, for the same reason: a merge preview must never be the thing that 500s the console.
public sealed class MergePreviewService(
    EasyDocsDbContext db, IBlobStore blobs, WmlComparerDiffService diff, ILogger<MergePreviewService> log)
{
    public record BaseSide(Guid Id, string Number);
    public record VersionSide(Guid Id, string Number, string AuthorName, ChangeSummary? Summary);

    // Available == can main <-> incoming be compared, i.e. WILL the merge work. Every other field
    // degrades on its own: a failed base leg costs a panel, not the merge.
    public record Preview(
        bool Available, BaseSide? Base, VersionSide Main, VersionSide Incoming,
        IReadOnlyList<ThreeWayOverlap.Paragraph>? Overlaps);

    // null => the merge cannot be attempted at all; the endpoint turns that into the same 409 the POST
    // would have returned.
    public async Task<Preview?> BuildAsync(Guid documentId, Guid leftId, Guid rightId, CancellationToken ct)
    {
        var sides = await MergeSides.ResolveAsync(db, documentId, leftId, rightId, ct);
        if (sides is null) return null;
        var (incoming, incomingBranch, mainHead, _) = sides;

        var names = await AuthorNames.ForAsync(db, [mainHead.CreatedBy, incoming.CreatedBy], ct);
        string Who(Guid id) => names.GetValueOrDefault(id, AuthorNames.Unknown);
        static string Num(Domain.DocumentVersion v) => $"{v.Major}.{v.Minor}.{v.Revision}";

        // Does the merge itself have a chance? Same pair, same engine, same bytes as MergeAsync — so
        // this predicts the merge exactly rather than guessing at it.
        var mergeable = await diff.SummaryAsync(mainHead.BlobSha256, incoming.BlobSha256, ct);

        // The fork point. Always present for a Concurrent branch (CommitSaveAsync only creates one when
        // BaseVersionId is set); null is the guard for IncomingPush and legacy rows.
        var baseVersion = incomingBranch.RootVersionId is { } rootId
            ? await db.Versions.FirstOrDefaultAsync(v => v.Id == rootId, ct)
            : null;

        if (baseVersion is null)
            return new Preview(
                mergeable.Available, null,
                new VersionSide(mainHead.Id, Num(mainHead), Who(mainHead.CreatedBy), null),
                new VersionSide(incoming.Id, Num(incoming), Who(incoming.CreatedBy), null),
                null);

        var mainLeg = await diff.SummaryAsync(baseVersion.BlobSha256, mainHead.BlobSha256, ct);
        var incomingLeg = await diff.SummaryAsync(baseVersion.BlobSha256, incoming.BlobSha256, ct);

        return new Preview(
            mergeable.Available,
            new BaseSide(baseVersion.Id, Num(baseVersion)),
            new VersionSide(mainHead.Id, Num(mainHead), Who(mainHead.CreatedBy), Summary(mainLeg)),
            new VersionSide(incoming.Id, Num(incoming), Who(incoming.CreatedBy), Summary(incomingLeg)),
            // Overlap needs BOTH base legs to have compared; without both there is nothing to intersect.
            mainLeg.Available && incomingLeg.Available
                ? await OverlapsAsync(baseVersion.BlobSha256, mainHead.BlobSha256, incoming.BlobSha256, ct)
                : null);
    }

    private static ChangeSummary? Summary(WmlComparerDiffService.DiffSummary s) =>
        s.Available ? new ChangeSummary(s.Insertions, s.Deletions, s.Moves, s.FormatChanges) : null;

    // Re-runs both base comparisons to get the compared PACKAGES — SummaryAsync only returns counts, and
    // the walker needs the XML. Not cached: the redline cache stores rendered html/docx, not the
    // in-memory WmlDocument, and adding a third cached artefact for a screen this size is not worth it.
    //
    // ponytail: two extra Compare calls per preview open. Ceiling: on a very large document that is the
    // slowest thing the screen does. Upgrade path if it bites — cache the overlap list in version_diffs
    // keyed by the (base, main, incoming) sha triple.
    private async Task<IReadOnlyList<ThreeWayOverlap.Paragraph>?> OverlapsAsync(
        string baseSha, string mainSha, string incomingSha, CancellationToken ct)
    {
        try
        {
            var b = await ReadAsync(baseSha, ct);
            var main = WmlComparer.Compare(new WmlDocument("base.docx", b),
                new WmlDocument("main.docx", await ReadAsync(mainSha, ct)), new WmlComparerSettings());
            var incoming = WmlComparer.Compare(new WmlDocument("base.docx", b),
                new WmlDocument("incoming.docx", await ReadAsync(incomingSha, ct)), new WmlComparerSettings());
            return ThreeWayOverlap.Find(main, incoming);
        }
        catch (Exception ex)
        {
            // Degrade, never throw: the redlines and counts are still worth showing without the hint.
            log.LogWarning(ex, "Three-way overlap failed for {Base}/{Main}/{Incoming}", baseSha, mainSha, incomingSha);
            return null;
        }
    }

    private async Task<byte[]> ReadAsync(string sha, CancellationToken ct)
    {
        await using var s = await blobs.OpenReadAsync(sha, ct);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
```

- [ ] **Step 2: Register it**

In `Program.cs`, immediately after the `builder.Services.AddScoped<WmlComparerMergeService>();` line:

```csharp
builder.Services.AddScoped<MergePreviewService>(); // read-only three-way review of a pending merge
```

- [ ] **Step 3: Verify it compiles**

Run: `dotnet build -c Release`
Expected: no errors. (`TreatWarningsAsErrors` is on — an unused using is a build failure.)

- [ ] **Step 4: Commit**

```bash
git add src/EasyDocs.Api/Merging/MergePreviewService.cs src/EasyDocs.Api/Program.cs
git commit -s -m "feat(merging): a read-only three-way preview of a pending merge"
```

---

## Task 4: The endpoint

**Files:**
- Modify: `src/EasyDocs.Api/Merging/MergeEndpoints.cs`
- Test: `tests/EasyDocs.Api.Tests/MergePreviewTests.cs`

- [ ] **Step 1: Write the failing tests**

Model the fixture helpers on `MergeTests.cs` — copy its `RegisterAsync`, `Docx` and `AddMemberAsync`
helpers and its `IClassFixture<ApiFactory>` shape, plus whatever it does to produce a concurrent
branch. Do **not** refactor `MergeTests` to share them: it is the guard suite for Task 1 and must stay
byte-identical.

```csharp
public class MergePreviewTests : IClassFixture<ApiFactory>
{
    // ... helpers copied from MergeTests ...

    [Fact] public async Task Editor_gets_a_preview_with_base_and_both_sides() { /* 200; base non-null; both summaries non-null */ }
    [Fact] public async Task Viewer_is_forbidden() { /* 403 -- a preview of a write needs the write's role */ }
    [Fact] public async Task Non_member_gets_404() { }
    [Fact] public async Task Unknown_document_is_404() { }
    [Fact] public async Task A_version_from_another_document_is_409() { }
    [Fact] public async Task Neither_side_on_an_incoming_branch_is_409() { }
    [Fact] public async Task Null_root_version_yields_null_base_but_stays_available()
    {
        // Set the concurrent Branch.RootVersionId to null directly via the DbContext, the way
        // MergeTests reaches in to build state. base == null, available == true, overlaps == null.
    }
    [Fact] public async Task A_malformed_incoming_blob_yields_available_false()
    {
        // DocxFixtures.Malformed() as the incoming version's bytes -- the merge would 409, so the
        // preview must say so rather than let the user click into a guaranteed failure.
    }
    [Fact] public async Task The_preview_writes_no_version_and_no_audit_row()
    {
        // Count Versions and AuditEvents for the document before and after; both unchanged.
        // This is the assertion that keeps a GET a GET.
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/EasyDocs.Api.Tests --filter "FullyQualifiedName~MergePreviewTests"`
Expected: FAIL — 404 from an unmapped route.

- [ ] **Step 3: Map the route**

In `MapMergeEndpoints`, beneath the existing `MapPost`:

```csharp
        // A GET, not ?dryRun= on the POST: the preview is a pure read — cacheable, revisitable,
        // linkable — and making a read look like a write to save a route entry is a bad trade.
        app.MapGet("/api/v1/documents/{id:guid}/merges/preview", Preview)
            .RequireAuthorization().WithTags("Merging");
```

And the handler:

```csharp
    // GET /api/v1/documents/{id}/merges/preview?left=&right=  — what the merge would do, before doing
    // it. Editor+, the same role the merge needs: a Viewer who cannot merge has no business enumerating
    // what a merge would collide with. Commits nothing.
    private static async Task<IResult> Preview(
        Guid id, Guid left, Guid right, HttpContext ctx, EasyDocsDbContext db, MergePreviewService preview)
    {
        var orgId = CurrentUser.OrgId(ctx.User);
        var userId = CurrentUser.UserId(ctx.User);
        var (result, role) = await DocumentAuthorization.ResolveAsync(db, orgId, userId, id, ctx.RequestAborted);
        if (result == AccessResult.NotFound) return Problem.Of(404, "Not found", "Document not found.");
        if (result == AccessResult.Forbidden) return Problem.Of(403, "Forbidden", "You do not have access to this document.");
        if (!DocumentAuthorization.CanEdit(role!.Value)) return Problem.Of(403, "Forbidden", "Editor role required.");

        var p = await preview.BuildAsync(id, left, right, ctx.RequestAborted);
        // Same title and detail the POST returns, so the screen shows the real message BEFORE the click
        // rather than after it.
        return p is null
            ? Problem.Of(409, "Merge unavailable", "Comparison failed — download both versions and merge manually.")
            : Results.Ok(p);
    }
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/EasyDocs.Api.Tests --filter "FullyQualifiedName~MergePreviewTests"`
Expected: PASS.

- [ ] **Step 5: Regenerate the committed OpenAPI snapshot — this WILL fail otherwise**

`OpenApiTests.Openapi_snapshot_in_docs_site_matches_the_served_document` asserts that
`docs-site/docs/api/openapi/v1.json` byte-matches the served `/openapi/v1.json`. The docs site publishes
that committed snapshot because the mkdocs job is Python-only and cannot boot the app, and this test is
what stops it rotting. **A new endpoint changes the served document, so this test fails until the
snapshot is regenerated.** That is expected, not a mistake.

Run: `UPDATE_OPENAPI_SNAPSHOT=1 dotnet test tests/EasyDocs.Api.Tests --filter Openapi_snapshot_in_docs_site_matches`

Then inspect the diff — it should contain **only** the new `/api/v1/documents/{id}/merges/preview` path
and its schema. Anything else in the diff means something unintended changed about the API surface;
stop and report it.

```bash
git diff --stat docs-site/docs/api/openapi/v1.json
```

- [ ] **Step 6: Run the whole C# suite**

Run: `dotnet test tests/EasyDocs.Api.Tests`
Expected: PASS, **0 skipped**. CI fails on a skipped test, so a skip is a failure here too.

- [ ] **Step 7: Commit**

```bash
git add src/EasyDocs.Api/Merging/MergeEndpoints.cs tests/EasyDocs.Api.Tests/MergePreviewTests.cs \
       docs-site/docs/api/openapi/v1.json
git commit -s -m "feat(api): GET /documents/{id}/merges/preview

Editor+, commits nothing. Every field degrades independently; only
'available' -- can main <-> incoming be compared -- predicts whether the
merge itself will work."
```

---

## Task 5: Share the concurrent-branch e2e helper

**Files:**
- Modify: `web/e2e/fixtures.ts`, `web/e2e/console.spec.ts:36-72`

Two specs now need a concurrent branch, and `raceConcurrentBranch` currently lives in `console.spec.ts`.
Move it (and the `mintSession` / `wopiSave` helpers it depends on) to `fixtures.ts`, which is already
where shared seeding lives.

- [ ] **Step 1: Move `Session`, `mintSession`, `wopiSave`, `raceConcurrentBranch` into `fixtures.ts`**

Move them verbatim, **including their comments** — especially the one explaining that Collabora is not
running and is not needed because WOPI is a server-to-server contract. Export each.

- [ ] **Step 2: Import them in `console.spec.ts`**

Add to the existing `./fixtures` import; delete the local definitions.

- [ ] **Step 3: Verify nothing broke**

Run: `npm --prefix web run e2e -- console.spec.ts`
Expected: PASS, unchanged. (Requires the API on :8080.)

- [ ] **Step 4: Commit**

```bash
git add web/e2e/fixtures.ts web/e2e/console.spec.ts
git commit -s -m "test(e2e): move the concurrent-branch helper into fixtures

The merge-review spec needs the same branch console.spec builds."
```

---

## Task 6: The review screen

**Files:**
- Modify: `web/src/api.ts`, `web/src/App.tsx`
- Create: `web/src/routes/MergeReview.tsx`

- [ ] **Step 1: Add the types to `api.ts`**

Beneath the existing `ChangeSummary` type, keeping the hand-maintained convention already documented
there:

```ts
// GET /api/v1/documents/{id}/merges/preview — the three-way review (spec:
// 2026-08-24-three-way-merge-review-design.md). Every field degrades on its own: `base` null means the
// fork point is unknown and the review falls back to a two-way preview; a null `summary` means that leg
// could not be compared; null `overlaps` means the hint is unavailable. Only `available: false` means
// the MERGE would fail.
export type MergeSideRow = {
  id: string
  number: string
  authorName: string
  summary: ChangeSummary | null
}
export type OverlapParagraph = { ordinal: number; text: string }
export type MergePreview = {
  available: boolean
  base: { id: string; number: string } | null
  main: MergeSideRow
  incoming: MergeSideRow
  overlaps: OverlapParagraph[] | null
}
```

- [ ] **Step 2: Add the route in `App.tsx`**

Beside the existing compare route, inside the same `RequireAuth`/`Shell` block:

```tsx
          <Route path="/documents/:id/merge" element={<MergeReview />} />
```

with the matching import.

- [ ] **Step 3: Write the screen**

```tsx
import { useEffect, useState } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router'
import { api, getRaw, problemText, type MergePreview } from '../api'

// The three-way review a merge now goes through (spec:
// 2026-08-24-three-way-merge-review-design.md). Read-only until the user commits: opening this screen
// changes nothing, and the Merge button posts the SAME request the History button used to.

// Word's redline colours, matching Compare.tsx — insertions red-underlined, deletions red-struck.
const REDLINE_STYLE =
  '<style>ins{color:#b3261e;text-decoration:underline}del{color:#b3261e;text-decoration:line-through}</style>'

export default function MergeReview() {
  const { id } = useParams()
  const [params] = useSearchParams()
  const navigate = useNavigate()
  const left = params.get('left') ?? ''
  const right = params.get('right') ?? ''

  const [preview, setPreview] = useState<MergePreview | null>(null)
  const [redlines, setRedlines] = useState<{ main: string; incoming: string } | null>(null)
  const [error, setError] = useState('')
  const [merging, setMerging] = useState(false)

  useEffect(() => {
    if (!id || !left || !right) return
    let live = true
    api
      .get<MergePreview>(`/api/v1/documents/${id}/merges/preview?left=${left}&right=${right}`)
      .then((p) => {
        if (!live) return
        setPreview(p)
        // Only fetch redlines once the base is known — without it there is nothing to diff against,
        // and the screen falls back to counts alone.
        if (!p.base) return
        const at = (to: string) =>
          getRaw(`/api/v1/documents/${id}/compare?from=${p.base!.id}&to=${to}&format=html`).then((r) =>
            r.text(),
          )
        return Promise.all([at(p.main.id), at(p.incoming.id)]).then(([main, incoming]) => {
          if (live) setRedlines({ main, incoming })
        })
      })
      .catch((e: unknown) => {
        if (live) setError(problemText(e, 'Could not preview this merge.'))
      })
    return () => {
      live = false
    }
  }, [id, left, right])

  const doMerge = () => {
    setMerging(true)
    setError('')
    api
      .post(`/api/v1/documents/${id}/merges`, { left, right })
      .then(() => navigate(`/documents/${id}`))
      .catch((e: unknown) => {
        setError(problemText(e, 'The merge failed.'))
        setMerging(false)
      })
  }

  const side = (label: string, s: MergePreview['main'], html: string | undefined) => (
    <section className="merge-side" data-testid={`merge-side-${label.toLowerCase()}`}>
      <h3>
        {label} · {s.number}
      </h3>
      <p className="muted">{s.authorName}</p>
      {s.summary ? (
        <p data-testid="merge-side-summary">
          {s.summary.insertions} insertions, {s.summary.deletions} deletions since the fork
        </p>
      ) : (
        <p className="muted">Changes since the fork could not be computed for this side.</p>
      )}
      {html !== undefined && (
        // SANDBOXED ON PURPOSE — see the identical note in Compare.tsx. This markup is generated by
        // WmlComparer from a user-uploaded .docx, so it is untrusted; inlining it would put
        // attacker-controlled markup on the origin that holds the session.
        <iframe
          className="editor-frame"
          title={`${label} changes since the fork`}
          sandbox=""
          srcDoc={REDLINE_STYLE + html}
        />
      )}
    </section>
  )

  return (
    <section data-testid="merge-review" className="merge-review">
      <h2>Review before merging</h2>
      <p>
        <Link to={`/documents/${id}`}>Back to the document</Link>
      </p>

      {error && (
        <p role="alert" className="error">
          {error}
        </p>
      )}

      {!preview && !error && <p>Preparing the review…</p>}

      {preview && (
        <>
          {preview.base ? (
            <p data-testid="merge-base">
              Both versions started from <strong>{preview.base.number}</strong>.
            </p>
          ) : (
            <p data-testid="merge-no-base" className="muted">
              The fork point for this branch is unknown, so only the merge itself can be summarised.
            </p>
          )}

          {preview.overlaps && preview.overlaps.length > 0 && (
            <section data-testid="merge-overlaps" className="merge-overlaps">
              <h3>⚠ Both authors changed {preview.overlaps.length} paragraph{preview.overlaps.length === 1 ? '' : 's'}</h3>
              <ul>
                {preview.overlaps.map((o) => (
                  <li key={o.ordinal}>{o.text}</li>
                ))}
              </ul>
              {/* Hedged for the same reason Compare.tsx hedges "no changes": the engine reads body text
                  only, and the ordinal anchor cannot survive a paragraph split. A hint that overclaims
                  would be worse than no hint. */}
              <p className="muted">
                A hint, not a guarantee — body-text paragraphs only. Review the changes below.
              </p>
            </section>
          )}

          <div className="merge-sides">
            {side('Main', preview.main, redlines?.main)}
            {side('Incoming', preview.incoming, redlines?.incoming)}
          </div>

          <p>
            Merging lands <strong>{preview.incoming.authorName}</strong>’s edits on the main history as
            tracked changes. Nothing is discarded, and the merge can be reverted.
          </p>

          {!preview.available && (
            <p role="alert" className="error" data-testid="merge-unavailable">
              These two versions cannot be compared, so the merge would fail. Download both from the
              history and merge them manually.
            </p>
          )}

          <div className="merge-actions">
            <button type="button" onClick={() => navigate(`/documents/${id}`)}>
              Cancel
            </button>
            <button type="button" onClick={doMerge} disabled={!preview.available || merging}>
              {merging ? 'Merging…' : 'Merge'}
            </button>
          </div>
        </>
      )}
    </section>
  )
}
```

- [ ] **Step 4: Add minimal styles to `web/src/index.css`**

Beside the existing `.compare` rules. Two side-by-side panels that stack on narrow screens, and an
overlap panel that reads as a warning. Reuse existing tokens — do not introduce new colours.

```css
.merge-sides { display: grid; grid-template-columns: repeat(auto-fit, minmax(20rem, 1fr)); gap: 1rem; }
.merge-overlaps { border-left: 3px solid currentColor; padding-left: 0.75rem; }
.merge-actions { display: flex; gap: 0.5rem; }
```

- [ ] **Step 5: Add the route to the e2e route table**

`web/e2e/routes.spec.ts` holds an explicit table asserting every route resolves, so a routing mistake
is ruled out before a screen's own spec runs. Add the new route to the `authenticated` array, after the
compare entry:

```ts
  [`/documents/${id}/merge`, 'merge-review'],
```

This works with the table's deliberately fake ids: with no `left`/`right` query params the fetch effect
returns early, so the screen renders its outer `data-testid="merge-review"` section and the
"Preparing the review…" line. That is exactly what this table checks — the route resolves — and nothing
more.

- [ ] **Step 6: Verify it builds and lints**

Run: `npm --prefix web run build && npm --prefix web run lint`
Expected: no errors.

- [ ] **Step 7: Commit**

```bash
git add web/src/api.ts web/src/App.tsx web/src/routes/MergeReview.tsx web/src/index.css \
       web/e2e/routes.spec.ts
git commit -s -m "feat(web): a three-way review screen for a pending merge"
```

---

## Task 7: Route the Merge button through it

**Files:**
- Modify: `web/src/routes/History.tsx`, `web/e2e/console.spec.ts:170-183`

- [ ] **Step 1: Replace the button with a link**

In `History.tsx`: delete the `merge` callback, and change `BranchGroup`'s `canMerge`/`onMerge` props to
a single `mainHead: string | undefined`. The button becomes:

```tsx
        // Merging is a decision, so it goes through the review screen rather than committing on click
        // (spec: 2026-08-24-three-way-merge-review-design.md). The POST itself is unchanged and still
        // lives on the API for callers that mean it.
        mainHead && (
          <Link
            className="button"
            to={`/documents/${documentId}/merge?left=${mainHead}&right=${group.rows[0].id}`}
          >
            Review &amp; merge
          </Link>
        )
```

`BranchGroup` needs `documentId`; it is already on `rowProps`.

- [ ] **Step 2: Update the existing e2e merge test**

`console.spec.ts`'s `'merging a concurrent branch adds a version and loses nothing (E4)'` currently
clicks Merge and expects `0.0.4`. It must now go through the review. Keep the E4 assertions — they are
the conformance-relevant part — and add the hop:

```ts
  await concurrentGroup(page).getByRole('link', { name: 'Review & merge' }).click()
  await expect(page.getByTestId('merge-review')).toBeVisible()
  await page.getByRole('button', { name: 'Merge' }).click()

  await expect(row(page, '0.0.4')).toBeVisible()
  // ... existing assertions unchanged ...
```

Also update `'a concurrent branch renders as an indented group with a Merge button (E4)'` to look for
the link rather than the button.

- [ ] **Step 3: Run the console suite**

Run: `npm --prefix web run e2e -- console.spec.ts`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add web/src/routes/History.tsx web/e2e/console.spec.ts
git commit -s -m "feat(web): merge goes through the review screen

The single unconfirmed click was safe -- nothing is destroyed -- but
blind: the user could not see what main had done since the fork."
```

---

## Task 8: End-to-end coverage for the new screen

**Files:**
- Create: `web/e2e/merge-review.spec.ts`

- [ ] **Step 1: Write the spec**

```ts
import { test, expect, raceConcurrentBranch } from './fixtures'

// The review a merge now goes through. raceConcurrentBranch produces main 0.0.2 (edited.docx) and a
// concurrent 0.0.3 (edited-plus-echo.docx), both forked from 0.0.1.

test('the Merge control opens the review and commits nothing', async ({ signedIn: page }) => {
  const documentId = await raceConcurrentBranch(page, 'Review')
  await page.goto(`/documents/${documentId}`)

  await page.getByRole('link', { name: 'Review & merge' }).click()
  await expect(page.getByTestId('merge-review')).toBeVisible()

  // Nothing was committed by opening the review.
  const res = await page.request.get(`/api/v1/documents/${documentId}/versions`)
  expect(((await res.json()) as { items: unknown[] }).items).toHaveLength(3)
})

test('the review names the fork point and both sides', async ({ signedIn: page }) => {
  const documentId = await raceConcurrentBranch(page, 'Sides')
  await page.goto(`/documents/${documentId}`)
  await page.getByRole('link', { name: 'Review & merge' }).click()

  await expect(page.getByTestId('merge-base')).toContainText('0.0.1')
  await expect(page.getByTestId('merge-side-main')).toContainText('0.0.2')
  await expect(page.getByTestId('merge-side-incoming')).toContainText('0.0.3')
  await expect(page.getByTestId('merge-side-main').getByTestId('merge-side-summary')).toBeVisible()
})

test('Cancel returns to the console with nothing merged', async ({ signedIn: page }) => {
  const documentId = await raceConcurrentBranch(page, 'Cancel')
  await page.goto(`/documents/${documentId}`)
  await page.getByRole('link', { name: 'Review & merge' }).click()
  await page.getByRole('button', { name: 'Cancel' }).click()

  await expect(page.getByTestId('history')).toBeVisible()
  await expect(page.getByRole('link', { name: 'Review & merge' })).toBeVisible()
})

test('Merge from the review commits and returns to the console', async ({ signedIn: page }) => {
  const documentId = await raceConcurrentBranch(page, 'Commit')
  await page.goto(`/documents/${documentId}`)
  await page.getByRole('link', { name: 'Review & merge' }).click()
  await page.getByRole('button', { name: 'Merge' }).click()

  await expect(page.getByTestId('history')).toBeVisible()
  await expect(page.locator('[data-testid="version-row"][data-number="0.0.4"]')).toBeVisible()
})
```

- [ ] **Step 2: Run it**

Run: `npm --prefix web run e2e -- merge-review.spec.ts`
Expected: PASS (4 tests).

**Note on the overlap panel:** `edited.docx` and `edited-plus-echo.docx` differ by an *appended*
paragraph, so they do not overlap and `merge-overlaps` will not render. Do **not** add a fixture pair
just to exercise it here — that case is already covered properly in `ThreeWayOverlapTests`, which tests
it directly and without a browser.

- [ ] **Step 3: Run the full web suite**

Run: `npm --prefix web run e2e`
Expected: PASS. `a11y.spec.ts` may need the new route added if it enumerates routes.

- [ ] **Step 4: Commit**

```bash
git add web/e2e/merge-review.spec.ts
git commit -s -m "test(e2e): cover the three-way merge review"
```

---

## Task 9: Documentation

**Files:**
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Add to `[Unreleased] → Added`**

Match the voice of the surrounding entries — say what changed, and be explicit about what did *not*:

```markdown
- **Merging a branch now goes through a review.** The history's Merge control opens a screen showing
  the version both branches forked from, what each side changed since that fork, and a hint naming the
  paragraphs both authors touched. `GET /api/v1/documents/{id}/merges/preview` returns the same
  information to API callers. The merge itself is unchanged — same two-way comparison, same tracked
  changes, same result — and `POST /api/v1/documents/{id}/merges` still merges directly, so existing
  scripts are unaffected.

  The overlap hint is best-effort and never blocks a merge: it anchors on paragraph position within
  the shared ancestor, which a paragraph split on one side can shift.
```

- [ ] **Step 2: Verify the full suite once more**

Run: `dotnet test tests/EasyDocs.Api.Tests && npm --prefix web run build && npm --prefix web run lint && npm --prefix web run e2e`
Expected: all PASS, **no skips** (CI fails on a skipped test).

- [ ] **Step 3: Commit**

```bash
git add CHANGELOG.md
git commit -s -m "docs: changelog for the three-way merge review"
```

---

## Definition of done

- [ ] `MergeTests.cs`, `PushMergeTests.cs`, `Conformance/E04_BranchMerge.cs` pass **with no edits**
- [ ] `POST /api/v1/documents/{id}/merges` unchanged in shape, auth and semantics
- [ ] Opening the review writes no version and no audit row (asserted, not assumed)
- [ ] The overlap panel is worded as a hint and never disables Merge
- [ ] `available: false` is the **only** condition that disables Merge
- [ ] No skipped tests anywhere in the suite
