using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Xml.Linq;
using EasyDocs.Api.Data;
using EasyDocs.Api.Storage;
using EasyDocs.Api.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EasyDocs.Api.Tests.Conformance;

// E4 Branch/merge (spec §12.1, §5.3): two sessions from one head -> two branches, zero lost edits;
// merge output opens with the INCOMING branch's changes as tracked changes attributed to their author,
// on top of current main; both branch versions persist; the merged branch closes.
//
// Both concurrent edits are made through real WOPI sessions, so this is the genuine two-editor race.
[Collection(ConformanceCollection.Name)]
public class E04_BranchMerge
{
    private readonly ApiFactory _f;
    public E04_BranchMerge(ApiFactory f) => _f = f;

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    // Alice owns the doc; Bob is an Editor. Both open a session on the SAME head, then both save.
    private async Task<(EdApi Alice, EdApi Bob, Guid DocId, Guid Head, Guid Left, Guid Right)> RaceAsync()
    {
        var alice = await EdApi.NewAsync(_f);
        var (docId, head) = await alice.NewDocumentWithBaseAsync("Concurrent", DocxFixtures.Base());

        var bob = await EdApi.ForSeededMemberAsync(_f, alice.OrgId);
        await alice.AddMemberAsync(docId, bob.Email, "Editor");

        // Two sessions minted from the same base version — neither editor knows about the other.
        var aliceSession = await alice.MintSessionAsync(head);
        var bobSession = await bob.MintSessionAsync(head);

        // Alice saves first: fast-forwards main.
        (await EdApi.WopiSaveAsync(_f.CreateClient(), aliceSession, DocxFixtures.Edited())).EnsureSuccessStatusCode();
        // Bob saves against a now-stale base: must branch instead of overwriting.
        (await EdApi.WopiSaveAsync(_f.CreateClient(), bobSession, DocxFixtures.EditedPlusEcho())).EnsureSuccessStatusCode();

        var (left, right) = await HeadsAsync(docId);
        return (alice, bob, docId, head, left, right);
    }

    // (main head, concurrent-branch head) straight from the DB — branch topology is deliberately not
    // part of the public v1 surface (indented-list history, no DAG endpoint).
    private async Task<(Guid Left, Guid Right)> HeadsAsync(Guid docId)
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();

        var main = await db.Branches.FirstAsync(b => b.DocumentId == docId && b.Ordinal == 0);
        var left = await db.Versions.Where(v => v.BranchId == main.Id)
            .OrderByDescending(v => v.SeqInBranch).Select(v => v.Id).FirstAsync();
        var right = await db.Versions.Where(v => v.DocumentId == docId && v.BranchId != main.Id)
            .OrderByDescending(v => v.SeqInBranch).Select(v => v.Id).FirstAsync();
        return (left, right);
    }

    [Fact]
    public async Task Two_sessions_from_one_head_produce_two_branches_with_zero_lost_edits()
    {
        var (alice, _, docId, head, left, right) = await RaceAsync();

        Assert.NotEqual(left, right);

        // All three versions survive: the base and both editors' work.
        var ids = (await alice.ListVersionsAsync(docId)).Items.Select(v => v.Id).ToHashSet();
        Assert.Contains(head, ids);
        Assert.Contains(left, ids);
        Assert.Contains(right, ids);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var branches = await db.Branches.Where(b => b.DocumentId == docId).ToListAsync();
        Assert.Equal(2, branches.Count); // main + one concurrent
        Assert.Contains(branches, b => b.Kind == EasyDocs.Api.Domain.BranchKind.Concurrent);

        // Neither save clobbered the other: distinct content.
        var shas = await db.Versions.Where(v => ids.Contains(v.Id)).Select(v => v.BlobSha256).ToListAsync();
        Assert.Equal(shas.Count, shas.Distinct().Count());
    }

    [Fact]
    public async Task Merge_puts_the_incoming_authors_changes_on_main_as_tracked_changes()
    {
        var (alice, bob, docId, _, left, right) = await RaceAsync();

        var res = await alice.MergeRawAsync(docId, left, right);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var mergeVersionId = (await res.Content.ReadFromJsonAsync<MergeResult>())!.MergeVersionId;

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();

        var mv = await db.Versions.FirstAsync(v => v.Id == mergeVersionId);
        var main = await db.Branches.FirstAsync(b => b.DocumentId == docId && b.Ordinal == 0);

        // A two-parent merge commit landing on main.
        Assert.Equal(main.Id, mv.BranchId);
        Assert.Equal(left, mv.ParentVersionId);
        Assert.Equal(right, mv.MergeParentVersionId);
        Assert.Equal(EasyDocs.Api.Domain.VersionSource.Merge, mv.Source);

        // The incoming branch is closed, pointing at the merge commit.
        var rightBranchId = (await db.Versions.FirstAsync(v => v.Id == right)).BranchId;
        Assert.Equal(mergeVersionId, (await db.Branches.FirstAsync(b => b.Id == rightBranchId)).MergedIntoVersionId);

        // Bob's distinctive edit is a tracked change attributed to Bob; Alice's edits are the clean base.
        var (trackedText, authors) = await RevisionsAsync(blobs, mv.BlobSha256);
        Assert.Contains("Echo", trackedText);
        Assert.DoesNotContain("Delta", trackedText);
        Assert.Contains("Seed", authors);      // Bob (seeded org user, DisplayName "Seed")
        Assert.DoesNotContain("U", authors);   // Alice (DisplayName "U") is accepted base, not tracked

        // Both pre-merge versions persist in history.
        var ids = (await alice.ListVersionsAsync(docId)).Items.Select(v => v.Id).ToHashSet();
        Assert.Contains(left, ids);
        Assert.Contains(right, ids);
        Assert.Contains(mergeVersionId, ids);
    }

    [Fact]
    public async Task Merge_requires_editor_role()
    {
        var (alice, _, docId, _, left, right) = await RaceAsync();

        var viewer = await EdApi.ForSeededMemberAsync(_f, alice.OrgId);
        await alice.AddMemberAsync(docId, viewer.Email, "Viewer");

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.MergeRawAsync(docId, left, right)).StatusCode);
    }

    private record MergeResult(Guid MergeVersionId);

    // Tracked-change text and the authors credited with it.
    private static async Task<(string Text, string[] Authors)> RevisionsAsync(IBlobStore blobs, string sha)
    {
        await using var stream = await blobs.OpenReadAsync(sha);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        using var zip = new ZipArchive(new MemoryStream(ms.ToArray()), ZipArchiveMode.Read);

        var entry = zip.GetEntry("word/document.xml")!;
        await using var docXml = entry.Open();
        var xml = XDocument.Load(docXml);

        var inserts = xml.Descendants(W + "ins").Concat(xml.Descendants(W + "del")).ToList();
        var text = string.Concat(inserts.SelectMany(i =>
            i.Descendants(W + "t").Concat(i.Descendants(W + "delText")).Select(t => t.Value)));
        var authors = inserts.Select(i => i.Attribute(W + "author")?.Value ?? "").Distinct().ToArray();
        return (text, authors);
    }
}
