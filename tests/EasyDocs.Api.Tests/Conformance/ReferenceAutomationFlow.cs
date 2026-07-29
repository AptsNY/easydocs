using System.Net.Http.Json;
using EasyDocs.Api.Tests.Fixtures;

namespace EasyDocs.Api.Tests.Conformance;

// THE M3 EXIT GATE: "the API drives the full document flow unattended".
//
// One script, one `ed_` personal access token, no browser and no session cookie: create -> upload ->
// edit/commit -> publish -> request approval -> respond -> share. Every step asserts its own effect, so
// a failure names the step that broke rather than just the end state.
//
// This is deliberately written the way a customer's integration would be written — only documented v1
// endpoints, only the PAT — so it doubles as the reference example for the public API.
[Collection(ConformanceCollection.Name)]
public class ReferenceAutomationFlow
{
    private readonly ApiFactory _f;
    public ReferenceAutomationFlow(ApiFactory f) => _f = f;

    private record PublicViewDto(string DocumentName, string Version, string DownloadUrl);

    [Fact]
    public async Task A_pat_alone_drives_create_upload_edit_publish_approve_share()
    {
        // --- 0. Credentials: mint a PAT once, then never touch the session again. -------------------
        var account = await _f.RegisterAsync();
        var pat = await _f.PatClientAsync(account.Client, "reference-automation");
        Assert.Equal("Bearer", pat.DefaultRequestHeaders.Authorization!.Scheme);
        Assert.StartsWith("ed_", pat.DefaultRequestHeaders.Authorization.Parameter);

        var robot = new AutomationClient(pat);

        // --- 1. Create a folder and a document. -----------------------------------------------------
        var folder = await robot.PostAsync<FolderDto>("/api/v1/folders", new { name = $"Automation {Guid.NewGuid():N}" });
        var doc = await robot.PostAsync<DocumentDto>("/api/v1/documents", new { name = "Unattended Lease", folderId = folder.Id });
        Assert.Equal(folder.Id, doc.FolderId);

        // --- 2. Upload the first .docx -> 0.0.1 (E2). -----------------------------------------------
        var first = await robot.UploadAsync(doc.Id, DocxFixtures.Base());
        Assert.Equal((0, 0, 1), (first.Major, first.Minor, first.Revision));

        // --- 3. Edit + commit through a real WOPI editing session (E3). -----------------------------
        var session = await robot.PostAsync<SessionDto>($"/api/v1/versions/{first.VersionId}/sessions", null);
        var saved = await EdApi.WopiSaveAsync(_f.CreateClient(), session, DocxFixtures.Edited());
        saved.EnsureSuccessStatusCode();

        var versions = await robot.GetAsync<VersionListDto>($"/api/v1/documents/{doc.Id}/versions?limit=100");
        Assert.Equal(2, versions.Items.Length);
        var head = versions.Items.OrderBy(v => v.Revision).Last();
        Assert.Equal((0, 0, 2), (head.Major, head.Minor, head.Revision));

        // Name the head so the published artifact is identifiable (E8 "Name").
        (await pat.PatchAsJsonAsync($"/api/v1/versions/{head.Id}", new { name = "Ready for signature" }))
            .EnsureSuccessStatusCode();

        // --- 4. Publish it (E6). ---------------------------------------------------------------------
        var published = await robot.PostAsync<PublishedDto>($"/api/v1/versions/{head.Id}/publish",
            new { kind = "minor", name = "v1 for counsel" });
        Assert.Equal((0, 1, 0), (published.Major, published.Minor, published.Revision));

        var publications = await robot.GetAsync<PublicationListDto>($"/api/v1/documents/{doc.Id}/publications");
        Assert.Equal(head.Id, Assert.Single(publications.Items).VersionId);

        // --- 5. Add an approver and request approval (E7). ------------------------------------------
        var approverAccount = await _f.SeedOrgUserAsync(account.OrgId);
        (await pat.PostAsJsonAsync($"/api/v1/documents/{doc.Id}/members", new { email = approverAccount.Email, role = "Editor" }))
            .EnsureSuccessStatusCode();

        var approvals = await robot.PostAsync<ApprovalDto[]>($"/api/v1/versions/{head.Id}/approvals",
            new { approverIds = new[] { approverAccount.UserId }, dueAt = DateTimeOffset.UtcNow.AddDays(2) });
        var approval = Assert.Single(approvals);
        Assert.NotNull(approval.DueAt);

        // --- 6. The approver responds — also over a PAT, from their own credential. ------------------
        var approverPat = await _f.PatClientAsync(approverAccount.Client, "approver-bot");
        var decision = await approverPat.PostAsJsonAsync($"/api/v1/approvals/{approval.Id}:respond",
            new { decision = "approved", comment = "Cleared by legal." });
        decision.EnsureSuccessStatusCode();
        Assert.Contains("approved", await decision.Content.ReadAsStringAsync());

        // --- 7. Share the published version and read it back with no credentials at all (E10). ------
        var share = await robot.PostAsync<ShareLinkDto>($"/api/v1/versions/{head.Id}/share-links",
            new { expiresAt = (DateTimeOffset?)null });

        var anonymous = _f.CreateClient();
        var view = await anonymous.GetFromJsonAsync<PublicViewDto>(share.Url);
        Assert.Equal("Unattended Lease", view!.DocumentName);
        Assert.Equal("0.1.0", view.Version);

        var downloaded = await anonymous.GetAsync(view.DownloadUrl);
        downloaded.EnsureSuccessStatusCode();
        Assert.NotEmpty(await downloaded.Content.ReadAsByteArrayAsync());

        // --- 8. The whole run is on the record (§11). -----------------------------------------------
        var trail = await robot.GetAsync<AuditListDto>($"/api/v1/documents/{doc.Id}/audit?limit=100");
        var actions = trail.Items.Select(i => i.Action).ToHashSet();
        foreach (var expected in new[]
        {
            "document.created", "version.created", "edit_session.opened", "version.named",
            "version.published", "member.added", "approval.requested", "approval.responded",
            "share_link.created", "share_link.viewed",
        })
            Assert.Contains(expected, actions);

        // Nothing in this run carried a session cookie — the PAT was the only credential.
        Assert.False(pat.DefaultRequestHeaders.Contains("Cookie"));
        Assert.False(approverPat.DefaultRequestHeaders.Contains("Cookie"));
    }

    // A deliberately thin wrapper: the flow above should read like a customer's script, not like
    // HTTP plumbing, but it must not hide which endpoint each step calls.
    private sealed class AutomationClient(HttpClient http)
    {
        public async Task<T> GetAsync<T>(string url)
        {
            var res = await http.GetAsync(url);
            res.EnsureSuccessStatusCode();
            return (await res.Content.ReadFromJsonAsync<T>())!;
        }

        public async Task<T> PostAsync<T>(string url, object? body)
        {
            var res = body is null ? await http.PostAsync(url, null) : await http.PostAsJsonAsync(url, body);
            if (!res.IsSuccessStatusCode)
                throw new HttpRequestException($"POST {url} -> {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
            return (await res.Content.ReadFromJsonAsync<T>())!;
        }

        public async Task<VersionRefDto> UploadAsync(Guid docId, byte[] bytes)
        {
            var res = await http.PostAsync($"/api/v1/documents/{docId}/versions", TestAuth.DocxForm(bytes));
            res.EnsureSuccessStatusCode();
            return (await res.Content.ReadFromJsonAsync<VersionRefDto>())!;
        }
    }
}
