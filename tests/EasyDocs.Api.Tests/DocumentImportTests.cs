using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EasyDocs.Api.Tests;

// One multipart call that creates a document and its first version (spec: import-document-as-new).
// Separate from DocumentUploadTests, which is about adding versions to a document that already exists.
public class DocumentImportTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public DocumentImportTests(ApiFactory f) => _f = f;

    private record ImportDto(Guid Id, string Name, Guid? FolderId, Guid VersionId, int Major, int Minor, int Revision);
    private record VersionDto(Guid Id, Guid DocumentId);
    private record DocRow(Guid Id, string Name);
    private record DocPage(List<DocRow> Items);

    // TestAuth.DocxForm hardcodes "f.docx", and half of what this endpoint does is derive a name from the
    // filename -- so these tests have to control it (and the optional fields) themselves.
    private static MultipartFormDataContent Form(byte[] bytes, string fileName, string? name = null, string? folderId = null)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(TestAuth.DocxMime);
        var form = new MultipartFormDataContent { { part, "file", fileName } };
        if (name is not null) form.Add(new StringContent(name), "name");
        if (folderId is not null) form.Add(new StringContent(folderId), "folderId");
        return form;
    }

    // The route carries a colon at the collection level, which is the one place the group prefix cannot
    // build it -- RouteGroupBuilder joins prefix and pattern with a slash, so mapping ":import" on the
    // group would yield /api/v1/documents/:import. Pinning reachability separately from behaviour means a
    // routing regression reads as a routing failure rather than as every import test failing at once.
    [Fact]
    public async Task The_import_route_exists_and_requires_authentication()
    {
        var anon = _f.CreateClient();
        var res = await anon.PostAsync("/api/v1/documents:import", TestAuth.DocxForm());

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task A_file_with_no_name_is_named_after_the_file_and_gets_version_0_0_1()
    {
        var acct = await _f.RegisterAsync();

        var res = await acct.Client.PostAsync("/api/v1/documents:import",
            Form(DocxFixtures.Build("Import default name"), "Signed Lease.docx"));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var dto = (await res.Content.ReadFromJsonAsync<ImportDto>())!;
        Assert.Equal("Signed Lease", dto.Name);
        Assert.Equal((0, 0, 1), (dto.Major, dto.Minor, dto.Revision));
    }

    [Fact]
    public async Task An_explicit_name_wins_over_the_filename()
    {
        var acct = await _f.RegisterAsync();

        var res = await acct.Client.PostAsync("/api/v1/documents:import",
            Form(DocxFixtures.Build("Import explicit name"), "whatever.docx", name: "Rider A"));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Equal("Rider A", (await res.Content.ReadFromJsonAsync<ImportDto>())!.Name);
    }

    // The case that is silently wrong if the filename is treated as a path: the tests run on Linux, where
    // Path.GetFileNameWithoutExtension does not treat `\` as a separator, so a browser sending a
    // Windows-style path would otherwise produce a document literally called `C:\docs\lease`.
    [Fact]
    public async Task A_windows_style_filename_keeps_only_its_last_segment()
    {
        var acct = await _f.RegisterAsync();

        var res = await acct.Client.PostAsync("/api/v1/documents:import",
            Form(DocxFixtures.Build("Import windows path"), @"C:\docs\lease.docx"));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Equal("lease", (await res.Content.ReadFromJsonAsync<ImportDto>())!.Name);
    }

    // A filename that is nothing but an extension leaves no stem at all, and the useful answer is to say so
    // rather than to invent a name like "Untitled" that nobody chose and nobody can search for.
    [Fact]
    public async Task A_filename_with_no_usable_stem_is_rejected()
    {
        var acct = await _f.RegisterAsync();

        var res = await acct.Client.PostAsync("/api/v1/documents:import",
            Form(DocxFixtures.Build("Import bare extension"), ".docx"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task A_whitespace_only_name_is_rejected()
    {
        var acct = await _f.RegisterAsync();

        var res = await acct.Client.PostAsync("/api/v1/documents:import",
            Form(DocxFixtures.Build("Import blank name"), "lease.docx", name: "   "));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        // The account is brand new, so anything it can list was created by that rejected call. Removing the
        // orphaned-empty-document failure mode is the whole point of this endpoint, and a rejection that
        // still leaves a document behind reintroduces it on the server side.
        Assert.Empty((await acct.Client.GetFromJsonAsync<DocPage>("/api/v1/documents?limit=100"))!.Items);
    }

    [Fact]
    public async Task A_folder_from_another_org_is_rejected_and_a_valid_one_is_honoured()
    {
        var mine = await _f.RegisterAsync();
        var theirs = await _f.RegisterAsync();
        var foreignFolder = await theirs.Client.CreateFolderAsync("Theirs");
        var myFolder = await mine.Client.CreateFolderAsync("Mine");

        var rejected = await mine.Client.PostAsync("/api/v1/documents:import",
            Form(DocxFixtures.Build("Import foreign folder"), "lease.docx", folderId: foreignFolder.ToString()));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        var accepted = await mine.Client.PostAsync("/api/v1/documents:import",
            Form(DocxFixtures.Build("Import own folder"), "lease.docx", folderId: myFolder.ToString()));
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var dto = (await accepted.Content.ReadFromJsonAsync<ImportDto>())!;
        Assert.Equal(myFolder, dto.FolderId);

        var page = await mine.Client.GetFromJsonAsync<DocPage>($"/api/v1/documents?folderId={myFolder}&limit=100");
        Assert.Equal(dto.Id, Assert.Single(page!.Items).Id);
    }

    // The three bodies that make reusing SaveAsync's hardening worth it rather than writing a fresh
    // handler: on a public endpoint each of these is an RFC-7807 400, and any of them turning into a 500
    // means an unhandled exception is reachable from an unauthenticated-shaped request.
    [Fact]
    public async Task A_malformed_body_is_a_400_and_never_a_500()
    {
        var acct = await _f.RegisterAsync();

        var json = await acct.Client.PostAsync("/api/v1/documents:import",
            new StringContent("{\"name\":\"Lease\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, json.StatusCode);

        var garbage = new StringContent("not a multipart body at all");
        garbage.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=--nonsense");
        var unparseable = await acct.Client.PostAsync("/api/v1/documents:import", garbage);
        Assert.Equal(HttpStatusCode.BadRequest, unparseable.StatusCode);

        var empty = await acct.Client.PostAsync("/api/v1/documents:import", Form([], "lease.docx"));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        Assert.Empty((await acct.Client.GetFromJsonAsync<DocPage>("/api/v1/documents?limit=100"))!.Items);
    }

    // Membership is per document (spec §11): an import is not org-visible just because the importer's
    // colleague is in the same org and never got invited.
    [Fact]
    public async Task The_importer_is_the_owner_and_an_uninvited_org_member_cannot_reach_it()
    {
        var owner = await _f.RegisterAsync();
        var res = await owner.Client.PostAsync("/api/v1/documents:import",
            Form(DocxFixtures.Build("Import membership"), "lease.docx"));
        var dto = (await res.Content.ReadFromJsonAsync<ImportDto>())!;

        var colleague = await _f.SeedOrgUserAsync(owner.OrgId);
        var theirView = await colleague.Client.GetAsync($"/api/v1/documents/{dto.Id}");
        Assert.Contains(theirView.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden });

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var member = await db.DocumentMembers.SingleAsync(m => m.DocumentId == dto.Id);
        Assert.Equal((owner.UserId, DocRole.Owner), (member.UserId, member.Role));
    }

    // The two halves of the import are written in two steps, so this is what proves they were actually
    // linked to each other rather than both merely existing.
    [Fact]
    public async Task The_returned_versionId_belongs_to_the_returned_document()
    {
        var acct = await _f.RegisterAsync();
        var res = await acct.Client.PostAsync("/api/v1/documents:import",
            Form(DocxFixtures.Build("Import version link"), "lease.docx"));
        var dto = (await res.Content.ReadFromJsonAsync<ImportDto>())!;

        var version = await acct.Client.GetFromJsonAsync<VersionDto>($"/api/v1/versions/{dto.VersionId}");
        Assert.Equal((dto.VersionId, dto.Id), (version!.Id, version.DocumentId));
    }

    [Fact]
    public async Task An_import_records_a_document_created_audit_row()
    {
        var acct = await _f.RegisterAsync();
        var res = await acct.Client.PostAsync("/api/v1/documents:import",
            Form(DocxFixtures.Build("Import audit"), "lease.docx"));
        var dto = (await res.Content.ReadFromJsonAsync<ImportDto>())!;

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        Assert.True(await db.AuditEvents.AnyAsync(a => a.DocumentId == dto.Id && a.Action == "document.created"));
    }
}
