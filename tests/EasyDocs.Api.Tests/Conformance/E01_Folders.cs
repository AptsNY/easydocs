using EasyDocs.Api.Tests.Fixtures;

namespace EasyDocs.Api.Tests.Conformance;

// E1 Folders (spec §12.1): nest >= 3 levels; move doc preserves history/members;
// delete prompts promote-vs-trash.
[Collection(ConformanceCollection.Name)]
public class E01_Folders
{
    private readonly ApiFactory _f;
    public E01_Folders(ApiFactory f) => _f = f;

    [Fact]
    public async Task Folders_nest_at_least_three_levels()
    {
        var api = await EdApi.NewAsync(_f);

        var l1 = await api.CreateFolderAsync("Clients");
        var l2 = await api.CreateFolderAsync("Acme", l1.Id);
        var l3 = await api.CreateFolderAsync("2026", l2.Id);
        var l4 = await api.CreateFolderAsync("Q3", l3.Id); // one deeper than the floor, to prove no cap

        Assert.Equal(l1.Id, l2.ParentId);
        Assert.Equal(l2.Id, l3.ParentId);
        Assert.Equal(l3.Id, l4.ParentId);

        // Each level is reachable by listing its parent.
        Assert.Contains(l2.Id, (await api.ListFoldersAsync(l1.Id)).Select(f => f.Id));
        Assert.Contains(l3.Id, (await api.ListFoldersAsync(l2.Id)).Select(f => f.Id));
        Assert.Contains(l4.Id, (await api.ListFoldersAsync(l3.Id)).Select(f => f.Id));

        // A document lives at the deepest level.
        var doc = await api.CreateDocumentAsync("Deep", l4.Id);
        Assert.Equal(l4.Id, (await api.GetDocumentAsync(doc.Id)).FolderId);
    }

    [Fact]
    public async Task Moving_a_document_preserves_its_history_and_members()
    {
        var api = await EdApi.NewAsync(_f);
        var from = await api.CreateFolderAsync("From");
        var to = await api.CreateFolderAsync("To");

        var doc = await api.CreateDocumentAsync("Contract", from.Id);
        await api.UploadAsync(doc.Id, DocxFixtures.Base());
        await api.UploadAsync(doc.Id, DocxFixtures.Edited());

        var collaborator = await _f.SeedOrgUserAsync(api.OrgId);
        await api.AddMemberAsync(doc.Id, collaborator.Email, "Editor");

        var historyBefore = (await api.ListVersionsAsync(doc.Id)).Items.Select(v => v.Id).ToArray();
        var membersBefore = (await api.ListMembersAsync(doc.Id)).Select(m => (m.UserId, m.Role)).OrderBy(x => x.UserId).ToArray();

        var moved = await api.MoveDocumentAsync(doc.Id, to.Id);
        Assert.Equal(to.Id, moved.FolderId);

        var historyAfter = (await api.ListVersionsAsync(doc.Id)).Items.Select(v => v.Id).ToArray();
        var membersAfter = (await api.ListMembersAsync(doc.Id)).Select(m => (m.UserId, m.Role)).OrderBy(x => x.UserId).ToArray();

        Assert.Equal(historyBefore, historyAfter);
        Assert.Equal(membersBefore, membersAfter);
    }

    [Fact]
    public async Task Deleting_a_non_empty_folder_requires_choosing_promote_or_trash()
    {
        var api = await EdApi.NewAsync(_f);
        var parent = await api.CreateFolderAsync("Parent");
        var child = await api.CreateFolderAsync("Child", parent.Id);

        // The prompt: refuse to guess when the folder is not empty.
        var noMode = await api.DeleteFolderRawAsync(parent.Id);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, noMode.StatusCode);
        Assert.Contains("promote_children", await noMode.Content.ReadAsStringAsync());

        // promote_children: the child survives, re-parented to the deleted folder's parent (root here).
        var promoted = await api.DeleteFolderRawAsync(parent.Id, "promote_children");
        Assert.Equal(System.Net.HttpStatusCode.NoContent, promoted.StatusCode);

        Assert.DoesNotContain(parent.Id, (await api.ListFoldersAsync()).Select(f => f.Id));
        var atRoot = await api.ListFoldersAsync();
        Assert.Contains(child.Id, atRoot.Select(f => f.Id));
        Assert.Null(atRoot.Single(f => f.Id == child.Id).ParentId);
    }

    [Fact]
    public async Task Trash_mode_removes_the_folder_and_leaves_children_in_place()
    {
        var api = await EdApi.NewAsync(_f);
        var parent = await api.CreateFolderAsync("Doomed");
        var child = await api.CreateFolderAsync("Kid", parent.Id);

        var trashed = await api.DeleteFolderRawAsync(parent.Id, "trash");
        Assert.Equal(System.Net.HttpStatusCode.NoContent, trashed.StatusCode);

        Assert.DoesNotContain(parent.Id, (await api.ListFoldersAsync()).Select(f => f.Id));
        // The child is not promoted — it stays under the trashed parent (documented trash semantics).
        Assert.Contains(child.Id, (await api.ListFoldersAsync(parent.Id)).Select(f => f.Id));
    }

    [Fact]
    public async Task An_empty_folder_deletes_without_a_mode()
    {
        var api = await EdApi.NewAsync(_f);
        var empty = await api.CreateFolderAsync("Empty");

        var res = await api.DeleteFolderRawAsync(empty.Id);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, res.StatusCode);
        Assert.DoesNotContain(empty.Id, (await api.ListFoldersAsync()).Select(f => f.Id));
    }
}
