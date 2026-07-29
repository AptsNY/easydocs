using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Xml.Linq;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Storage;
using EasyDocs.Api.Tests;
using EasyDocs.Api.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// Merging an accepted push into the target's main branch (spec §8 merge base, §5.3, E9).
//
// What the fork point actually buys: it is stored on the incoming branch as its RootVersionId, giving the
// branch a root inside the TARGET's own history so nothing has to walk into the copy document. Under
// merge-into-main (§5.3 [D]) the redline itself is Compare(main head, incoming) — the ancestor is
// provenance and topology, not an input to the comparison. See the ponytail note in
// WmlComparerMergeService.
public class PushMergeTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public PushMergeTests(ApiFactory f) => _f = f;

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private record CopyDto(Guid Id, Guid ForkedFromVersionId, Guid VersionId);
    private record PushDto(Guid Id, string Status, Guid? MaterializedVersionId);
    private record MergeDto(Guid MergeVersionId);

    // Alice's master, forked to a copy, redlined by a copy-only reviewer, pushed and accepted. The
    // returned MaterializedVersionId is the incoming_push head on the target.
    private sealed record Pushed(
        Account Alice, Account Reviewer, Guid MasterId, Guid MasterHead, Guid CopyId, Guid Materialized);

    private async Task<Pushed> AcceptedPushAsync()
    {
        var alice = await _f.RegisterAsync();
        var masterId = await alice.Client.CreateDocAsync("Master agreement");
        var (masterHead, _) = await alice.Client.UploadAsync(masterId, DocxFixtures.Base());

        var forkRes = await alice.Client.PostAsJsonAsync($"/api/v1/versions/{masterHead}/copies", new { name = "Counsel copy" });
        forkRes.EnsureSuccessStatusCode();
        var copy = (await forkRes.Content.ReadFromJsonAsync<CopyDto>())!;

        // The external reviewer belongs to the copy only, and does the redlining.
        var reviewer = await _f.SeedOrgUserAsync(alice.OrgId);
        (await alice.Client.PostAsJsonAsync($"/api/v1/documents/{copy.Id}/members",
            new { email = reviewer.Email, role = "Editor" })).EnsureSuccessStatusCode();
        var (redlined, _) = await reviewer.Client.UploadAsync(copy.Id, DocxFixtures.Edited());

        var pushRes = await reviewer.Client.PostAsJsonAsync($"/api/v1/documents/{copy.Id}/pushes",
            new { targetDocumentId = masterId, versionId = redlined });
        pushRes.EnsureSuccessStatusCode();
        var push = (await pushRes.Content.ReadFromJsonAsync<PushDto>())!;
        Assert.Equal("pending", push.Status);

        var acceptRes = await alice.Client.PostAsync($"/api/v1/push-requests/{push.Id}:accept", null);
        acceptRes.EnsureSuccessStatusCode();
        var accepted = (await acceptRes.Content.ReadFromJsonAsync<PushDto>())!;

        return new Pushed(alice, reviewer, masterId, masterHead, copy.Id, accepted.MaterializedVersionId!.Value);
    }

    [Fact]
    public async Task A_materialized_push_carries_the_fork_point_as_its_branch_root()
    {
        var p = await AcceptedPushAsync();

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();

        var incoming = await db.Versions.FirstAsync(v => v.Id == p.Materialized);
        var branch = await db.Branches.FirstAsync(b => b.Id == incoming.BranchId);
        var forkPoint = await db.Documents.Where(d => d.Id == p.CopyId)
            .Select(d => d.ForkedFromVersionId).FirstAsync();

        Assert.Equal(BranchKind.IncomingPush, branch.Kind);
        Assert.Equal(forkPoint, branch.RootVersionId);
        Assert.Equal(p.MasterHead, branch.RootVersionId); // the fork point, i.e. what the reviewer started from

        // The root is a version of the TARGET, so cross-document merge resolves it without walking into
        // the copy document (spec §8).
        Assert.Equal(p.MasterId, await db.Versions.Where(v => v.Id == branch.RootVersionId)
            .Select(v => v.DocumentId).FirstAsync());
    }

    [Fact]
    public async Task The_incoming_version_is_attributed_to_the_pusher_not_the_acceptor()
    {
        var p = await AcceptedPushAsync();

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var incoming = await db.Versions.FirstAsync(v => v.Id == p.Materialized);

        // Alice merely accepted it; the content is the reviewer's work and history must say so.
        Assert.Equal(p.Reviewer.UserId, incoming.CreatedBy);
    }

    [Fact]
    public async Task Merging_an_incoming_push_puts_the_pushers_changes_on_main_as_tracked_changes()
    {
        var p = await AcceptedPushAsync();

        var res = await p.Alice.Client.PostAsJsonAsync($"/api/v1/documents/{p.MasterId}/merges",
            new { left = p.MasterHead, right = p.Materialized });

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var mergeVersionId = (await res.Content.ReadFromJsonAsync<MergeDto>())!.MergeVersionId;

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();

        var main = await db.Branches.FirstAsync(b => b.DocumentId == p.MasterId && b.Ordinal == 0);
        var mv = await db.Versions.FirstAsync(v => v.Id == mergeVersionId);

        // A two-parent merge commit on main, with the incoming push as the merge parent.
        Assert.Equal(main.Id, mv.BranchId);
        Assert.Equal(p.MasterHead, mv.ParentVersionId);
        Assert.Equal(p.Materialized, mv.MergeParentVersionId);
        Assert.Equal(VersionSource.Merge, mv.Source);

        // The reviewer's edits arrive as tracked changes credited to the reviewer (DisplayName "Seed"),
        // never to Alice ("U"), whose content is the accepted base.
        var (trackedText, authors) = await RevisionsAsync(blobs, mv.BlobSha256);
        Assert.Contains("Delta", trackedText);
        Assert.Equal("Seed", Assert.Single(authors));

        // The incoming branch closes, pointing at the merge commit.
        var incomingBranchId = (await db.Versions.FirstAsync(v => v.Id == p.Materialized)).BranchId;
        Assert.Equal(mergeVersionId, (await db.Branches.FirstAsync(b => b.Id == incomingBranchId)).MergedIntoVersionId);

        // Both pre-merge versions persist in the target's history.
        Assert.True(await db.Versions.AnyAsync(v => v.Id == p.MasterHead));
        Assert.True(await db.Versions.AnyAsync(v => v.Id == p.Materialized));
    }

    [Fact]
    public async Task The_merge_never_needs_the_copy_document()
    {
        var p = await AcceptedPushAsync();

        // Trash the copy entirely: everything the merge needs is already on the target side (the incoming
        // branch's root and head), so it must still succeed (spec §8 resolution note).
        (await p.Alice.Client.DeleteAsync($"/api/v1/documents/{p.CopyId}")).EnsureSuccessStatusCode();

        var res = await p.Alice.Client.PostAsJsonAsync($"/api/v1/documents/{p.MasterId}/merges",
            new { left = p.MasterHead, right = p.Materialized });

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }

    // Tracked-change text and the authors credited with it (same reader as E04).
    private static async Task<(string Text, string[] Authors)> RevisionsAsync(IBlobStore blobs, string sha)
    {
        await using var stream = await blobs.OpenReadAsync(sha);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        using var zip = new ZipArchive(new MemoryStream(ms.ToArray()), ZipArchiveMode.Read);

        await using var docXml = zip.GetEntry("word/document.xml")!.Open();
        var xml = XDocument.Load(docXml);

        var revisions = xml.Descendants(W + "ins").Concat(xml.Descendants(W + "del")).ToList();
        var text = string.Concat(revisions.SelectMany(r =>
            r.Descendants(W + "t").Concat(r.Descendants(W + "delText")).Select(t => t.Value)));
        var authors = revisions.Select(r => r.Attribute(W + "author")?.Value ?? "").Distinct().ToArray();
        return (text, authors);
    }
}
