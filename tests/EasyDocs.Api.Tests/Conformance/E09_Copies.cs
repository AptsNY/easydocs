using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Xml.Linq;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Storage;
using EasyDocs.Api.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EasyDocs.Api.Tests.Conformance;

// E9 Copies (spec §12.1): isolated members/history; non-member push -> pending review; accept ->
// incoming branch; reject -> hidden + pusher notified; merge into main via the fork-point ancestor.
//
// The whole criterion is driven through the public v1 API with `ed_` PATs. Two documented deviations,
// both matching the rest of the suite (see ConformanceFixture):
//   * Branch topology is deliberately not part of the v1 surface (indented-list history, no DAG
//     endpoint), so assertions ABOUT branches read the DB to verify. Every action still goes over HTTP.
//   * "Pusher notified" is asserted by subscribing to the SSE EventBus in-process, because TestServer
//     cannot read a second response while an /events stream is open (see SseTests).
[Collection(ConformanceCollection.Name)]
public class E09_Copies
{
    private readonly ApiFactory _f;
    public E09_Copies(ApiFactory f) => _f = f;

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    // The E9 scenario: a master owned by an internal author, forked to a copy, with an external reviewer
    // who is a member of the COPY ONLY and has redlined it.
    private sealed record Scenario(
        EdApi Author, EdApi Reviewer, Guid MasterId, Guid MasterHead, Guid CopyId, Guid Redlined);

    private async Task<Scenario> ScenarioAsync()
    {
        var author = await EdApi.NewAsync(_f);
        var (masterId, masterHead) = await author.NewDocumentWithBaseAsync("Master agreement", DocxFixtures.Base());

        var copy = await author.ForkAsync(masterHead, "Outside counsel copy");

        var reviewer = await EdApi.ForSeededMemberAsync(_f, author.OrgId);
        await author.AddMemberAsync(copy.Id, reviewer.Email, "Editor");
        var redlined = await reviewer.UploadAsync(copy.Id, DocxFixtures.Edited());

        return new Scenario(author, reviewer, masterId, masterHead, copy.Id, redlined.VersionId);
    }

    [Fact]
    public async Task Copy_has_isolated_members_and_history()
    {
        var s = await ScenarioAsync();

        // Isolated members: the copy's roster is its own. The author is there because they forked it; the
        // reviewer because they were invited to the copy. Neither implies anything about the other document.
        var copyMembers = await s.Author.ListMembersAsync(s.CopyId);
        var masterMembers = await s.Author.ListMembersAsync(s.MasterId);
        Assert.Equal(
            new[] { s.Author.UserId, s.Reviewer.UserId }.Order(),
            copyMembers.Select(m => m.UserId).Order());
        Assert.Equal(s.Author.UserId, Assert.Single(masterMembers).UserId);
        Assert.DoesNotContain(s.Reviewer.UserId, masterMembers.Select(m => m.UserId));

        // Isolated history: the copy numbers from scratch (0.0.1 fork, 0.0.2 redline) and the redline is
        // in the copy's version list, not the master's.
        var copyVersions = (await s.Author.ListVersionsAsync(s.CopyId)).Items;
        var masterVersions = (await s.Author.ListVersionsAsync(s.MasterId)).Items;
        Assert.Equal([(0, 0, 1), (0, 0, 2)], copyVersions.Select(v => (v.Major, v.Minor, v.Revision)).ToArray());
        Assert.Equal(s.MasterHead, Assert.Single(masterVersions).Id);
        Assert.DoesNotContain(s.Redlined, masterVersions.Select(v => v.Id));

        // The fork is discoverable from the master and points back at the version it came from.
        var copy = Assert.Single(await s.Author.ListCopiesAsync(s.MasterId));
        Assert.Equal(s.CopyId, copy.Id);
        Assert.Equal(s.MasterId, copy.ParentDocumentId);
        Assert.Equal(s.MasterHead, copy.ForkedFromVersionId);
    }

    [Fact]
    public async Task Non_member_push_creates_a_pending_review()
    {
        var s = await ScenarioAsync();

        // The reviewer holds no role at all on the master...
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Reviewer.Http.GetAsync($"/api/v1/documents/{s.MasterId}")).StatusCode);
        // ...yet may push, because a push is authorized on the source copy (spec §8/§11).
        var push = await s.Reviewer.PushAsync(s.CopyId, s.MasterId, s.Redlined);

        Assert.Equal("pending", push.Status);
        Assert.Null(push.MaterializedVersionId);
        Assert.Equal(s.Reviewer.UserId, push.PushedBy);

        // The target's members see it waiting for review.
        Assert.Equal(push.Id, Assert.Single(await s.Author.ListPushRequestsAsync(s.MasterId, "pending")).Id);

        // Nothing has entered the master's history while it waits.
        Assert.Equal(s.MasterHead, Assert.Single((await s.Author.ListVersionsAsync(s.MasterId)).Items).Id);
    }

    [Fact]
    public async Task Accepting_a_push_lands_it_on_an_incoming_branch()
    {
        var s = await ScenarioAsync();
        var push = await s.Reviewer.PushAsync(s.CopyId, s.MasterId, s.Redlined);

        var accepted = await s.Author.DecidePushAsync(push.Id, "accept");

        Assert.Equal("accepted", accepted.Status);
        Assert.NotNull(accepted.MaterializedVersionId);

        // It is in the master's history now, credited to the reviewer who wrote it — not to the acceptor.
        var landed = await s.Author.GetVersionAsync(accepted.MaterializedVersionId!.Value);
        Assert.Equal(s.MasterId, landed.DocumentId);
        Assert.Equal(s.Reviewer.UserId, landed.CreatedBy);
        Assert.Equal("CopyPush", landed.Source);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var branch = await db.Branches.FirstAsync(b => b.Id ==
            db.Versions.Where(v => v.Id == landed.Id).Select(v => v.BranchId).First());

        // An incoming_push branch, rendered distinctly from main: it is not main, and its root is the fork
        // point rather than the current main head, so it hangs off where the reviewer actually started.
        Assert.Equal(BranchKind.IncomingPush, branch.Kind);
        Assert.NotEqual(0, branch.Ordinal);
        Assert.Equal(s.MasterHead, branch.RootVersionId);
        Assert.Null(branch.MergedIntoVersionId);
    }

    [Fact]
    public async Task Rejecting_a_push_hides_it_and_notifies_the_pusher()
    {
        var s = await ScenarioAsync();
        var push = await s.Reviewer.PushAsync(s.CopyId, s.MasterId, s.Redlined);

        // Notified on the COPY — the pusher holds no target role, so a target-side event never reaches them.
        var events = await _f.CaptureEventsAsync(s.CopyId,
            () => s.Author.DecidePushRawAsync(push.Id, "reject"), until: "push.reviewed");
        Assert.Contains("push.reviewed", events);

        // Hidden: nothing entered the master's history, and no branch was created for it.
        Assert.Equal(s.MasterHead, Assert.Single((await s.Author.ListVersionsAsync(s.MasterId)).Items).Id);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        Assert.False(await db.Branches.AnyAsync(b => b.DocumentId == s.MasterId && b.Kind == BranchKind.IncomingPush));

        // The decision is final and readable by the pusher on the copy they do belong to.
        var seenByPusher = Assert.Single(await s.Reviewer.ListPushRequestsAsync(s.CopyId));
        Assert.Equal("rejected", seenByPusher.Status);
        Assert.Null(seenByPusher.MaterializedVersionId);
        Assert.NotNull(seenByPusher.DecidedAt);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Author.DecidePushRawAsync(push.Id, "accept")).StatusCode);
    }

    [Fact]
    public async Task Push_merges_into_main_via_the_fork_point_ancestor()
    {
        var s = await ScenarioAsync();
        var push = await s.Reviewer.PushAsync(s.CopyId, s.MasterId, s.Redlined);
        var incoming = (await s.Author.DecidePushAsync(push.Id, "accept")).MaterializedVersionId!.Value;

        var res = await s.Author.MergeRawAsync(s.MasterId, s.MasterHead, incoming);

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();

        var main = await db.Branches.FirstAsync(b => b.DocumentId == s.MasterId && b.Ordinal == 0);
        var merge = await db.Versions.Where(v => v.BranchId == main.Id)
            .OrderByDescending(v => v.SeqInBranch).FirstAsync();
        var incomingBranch = await db.Branches.FirstAsync(b => b.Id ==
            db.Versions.Where(v => v.Id == incoming).Select(v => v.BranchId).First());

        // The ancestor the cross-document merge resolves is the fork point, carried on the incoming branch
        // and living in the TARGET's history — so resolving it never walks into the copy document (§8).
        Assert.Equal(s.MasterHead, incomingBranch.RootVersionId);
        Assert.Equal(s.MasterId, await db.Versions.Where(v => v.Id == incomingBranch.RootVersionId)
            .Select(v => v.DocumentId).FirstAsync());

        // Merged onto main as a two-parent commit, and the incoming branch closes.
        Assert.Equal(VersionSource.Merge, merge.Source);
        Assert.Equal(incoming, merge.MergeParentVersionId);
        Assert.Equal(merge.Id, incomingBranch.MergedIntoVersionId);

        // The reviewer's edits arrive as tracked changes attributed to the reviewer, ready to accept/reject
        // on top of current main (spec §5.3).
        var (trackedText, authors) = await RevisionsAsync(blobs, merge.BlobSha256);
        Assert.Contains("Delta", trackedText);
        Assert.Equal("Seed", Assert.Single(authors)); // the seeded reviewer's DisplayName

        // Both pre-merge versions persist in history.
        var ids = (await s.Author.ListVersionsAsync(s.MasterId)).Items.Select(v => v.Id).ToHashSet();
        Assert.Contains(s.MasterHead, ids);
        Assert.Contains(incoming, ids);
        Assert.Contains(merge.Id, ids);
    }

    [Fact]
    public async Task A_copy_never_leaks_master_drafts()
    {
        var s = await ScenarioAsync();

        // Internal drafts land on the master AFTER the fork. The copy's reviewer must not reach any of it.
        var draft = await s.Author.UploadAsync(s.MasterId, DocxFixtures.EditedPlusEcho());
        await s.Author.PublishRawAsync(draft.VersionId, "minor");

        foreach (var path in new[]
        {
            $"/api/v1/documents/{s.MasterId}",
            $"/api/v1/documents/{s.MasterId}/versions",
            $"/api/v1/documents/{s.MasterId}/publications",
            $"/api/v1/documents/{s.MasterId}/audit",
            $"/api/v1/documents/{s.MasterId}/members",
            $"/api/v1/documents/{s.MasterId}/copies",
            $"/api/v1/documents/{s.MasterId}/push-requests",
            $"/api/v1/documents/{s.MasterId}/events",
            $"/api/v1/versions/{draft.VersionId}",
            $"/api/v1/versions/{draft.VersionId}/download",
            $"/api/v1/documents/{s.MasterId}/compare?from={s.MasterHead}&to={draft.VersionId}",
        })
            Assert.Equal(HttpStatusCode.Forbidden, (await s.Reviewer.Http.GetAsync(path)).StatusCode);

        // Nor may they write to it, share it out, or fork it again.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await s.Reviewer.Http.PostAsync($"/api/v1/documents/{s.MasterId}/versions", TestAuth.DocxForm())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Reviewer.ForkRawAsync(draft.VersionId, "leak")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await s.Reviewer.Http.PostAsJsonAsync($"/api/v1/versions/{draft.VersionId}/share-links", new { })).StatusCode);

        // The one thing they CAN do to the master is push their own copy's work at it — and only that copy's
        // work, at only that master (the bypass is scoped, spec §8).
        var elsewhere = await s.Author.CreateDocumentAsync("Unrelated document");
        await s.Author.UploadAsync(elsewhere.Id, DocxFixtures.Base());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await s.Reviewer.PushRawAsync(s.CopyId, elsewhere.Id, s.Redlined)).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await s.Reviewer.PushRawAsync(s.CopyId, s.MasterId, s.Redlined)).StatusCode);
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
