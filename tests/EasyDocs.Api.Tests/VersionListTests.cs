using System.Net.Http.Json;
using EasyDocs.Api.Tests.Fixtures;

namespace EasyDocs.Api.Tests;

// The console version row (spec §9 "version list with per-row change summary", E3 "list shows
// author/time/summary"). Branch fields are on the v1 surface as of M4.5 because §9's grouped
// concurrent-branch rendering + Merge button cannot be built without them.
public class VersionListTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public VersionListTests(ApiFactory f) => _f = f;

    private record Row(
        Guid Id, int Major, int Minor, int Revision, string Number, string? Name, string Source,
        string? PublishedKind, DateTimeOffset? PublishedAt, string? PublishName, bool HasPdf,
        Guid? ParentVersionId, Guid BranchId, string BranchKind, int BranchOrdinal,
        Guid? BranchMergedIntoVersionId, Guid CreatedBy, string CreatedByName,
        DateTimeOffset CreatedAt, Summary? Summary);
    private record Summary(int Insertions, int Deletions, int Moves, int FormatChanges);
    private record Page(Row[] Items, string? NextCursor);

    [Fact]
    public async Task The_row_carries_branch_identity_publish_state_and_the_author_name()
    {
        var acct = await _f.RegisterAsync();
        var docId = await acct.Client.CreateDocAsync("Console row");
        var (v1, _) = await acct.Client.UploadAsync(docId, DocxFixtures.Base());
        var (v2, _) = await acct.Client.UploadAsync(docId, DocxFixtures.Edited());

        (await acct.Client.PatchAsJsonAsync($"/api/v1/versions/{v2}", new { name = "For review" }))
            .EnsureSuccessStatusCode();
        (await acct.Client.PostAsJsonAsync($"/api/v1/versions/{v2}/publish", new { kind = "minor" }))
            .EnsureSuccessStatusCode();

        var page = await acct.Client.GetFromJsonAsync<Page>($"/api/v1/documents/{docId}/versions?limit=100");

        var first = page!.Items.Single(r => r.Id == v1);
        var second = page.Items.Single(r => r.Id == v2);

        // Branch identity — the whole point of this task.
        Assert.Equal("Main", first.BranchKind);
        Assert.Equal(first.BranchId, second.BranchId);
        Assert.Equal(0, first.BranchOrdinal);
        Assert.Null(first.BranchMergedIntoVersionId);

        // Everything else the row must show without a second request per row.
        Assert.Equal("0.0.1", first.Number);
        Assert.Equal("For review", second.Name);
        Assert.Equal("minor", second.PublishedKind);
        Assert.NotNull(second.PublishedAt);
        Assert.Equal(v1, second.ParentVersionId);
        Assert.Equal("U", first.CreatedByName); // TestAuth registers displayName "U"
        Assert.Equal(acct.UserId, first.CreatedBy);
    }

    [Fact]
    public async Task Order_desc_returns_the_newest_version_first_and_the_default_stays_ascending()
    {
        var acct = await _f.RegisterAsync();
        var docId = await acct.Client.CreateDocAsync("Ordering");
        var (v1, _) = await acct.Client.UploadAsync(docId, DocxFixtures.Base());
        var (v2, _) = await acct.Client.UploadAsync(docId, DocxFixtures.Edited());

        var asc = await acct.Client.GetFromJsonAsync<Page>($"/api/v1/documents/{docId}/versions");
        var desc = await acct.Client.GetFromJsonAsync<Page>($"/api/v1/documents/{docId}/versions?order=desc");

        Assert.Equal(v1, asc!.Items.First().Id); // unchanged default — E-tests depend on it
        Assert.Equal(v2, desc!.Items.First().Id);
    }
}
