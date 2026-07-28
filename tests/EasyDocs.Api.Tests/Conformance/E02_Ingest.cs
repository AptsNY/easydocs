using System.Net.Http.Json;
using EasyDocs.Api.Tests.Fixtures;

namespace EasyDocs.Api.Tests.Conformance;

// E2 Ingest (spec §12.1): local upload ONLY; first version exactly 0.0.1.
[Collection(ConformanceCollection.Name)]
public class E02_Ingest
{
    private readonly ApiFactory _f;
    public E02_Ingest(ApiFactory f) => _f = f;

    [Fact]
    public async Task First_uploaded_version_is_exactly_0_0_1()
    {
        var api = await EdApi.NewAsync(_f);
        var doc = await api.CreateDocumentAsync("Fresh");

        var v = await api.UploadAsync(doc.Id, DocxFixtures.Base());

        Assert.Equal((0, 0, 1), (v.Major, v.Minor, v.Revision));
        var detail = await api.GetVersionAsync(v.VersionId);
        Assert.Equal("Upload", detail.Source);
        Assert.Null(detail.ParentVersionId); // genuinely the first
        Assert.Single((await api.ListVersionsAsync(doc.Id)).Items);
    }

    [Fact]
    public async Task Ingest_is_a_direct_multipart_upload_with_no_pre_signed_url_step()
    {
        // Spec §10.3: no :initiate/:commit, no upload_url — the body goes straight to the app.
        var api = await EdApi.NewAsync(_f);
        var doc = await api.CreateDocumentAsync("Direct");

        var initiate = await api.Http.PostAsJsonAsync($"/api/v1/documents/{doc.Id}/versions:initiate", new { });
        Assert.Equal(System.Net.HttpStatusCode.NotFound, initiate.StatusCode);

        // The documented path works in one call.
        var v = await api.UploadAsync(doc.Id, DocxFixtures.Base());
        Assert.Equal((0, 0, 1), (v.Major, v.Minor, v.Revision));
    }

    [Fact]
    public async Task Import_is_also_a_local_multipart_upload()
    {
        var api = await EdApi.NewAsync(_f);
        var doc = await api.CreateDocumentAsync("Imported");
        await api.UploadAsync(doc.Id, DocxFixtures.Base());

        var imported = await api.ImportAsync(doc.Id, DocxFixtures.Edited());

        Assert.Equal((0, 0, 2), (imported.Major, imported.Minor, imported.Revision));
        Assert.Equal("Import", (await api.GetVersionAsync(imported.VersionId)).Source);
    }

    // A public ingest endpoint must answer a bad body with RFC-7807, never a 500.
    [Fact]
    public async Task An_empty_or_missing_file_is_rejected()
    {
        var api = await EdApi.NewAsync(_f);
        var doc = await api.CreateDocumentAsync("Empty");

        var res = await api.Http.PostAsync($"/api/v1/documents/{doc.Id}/versions", new MultipartFormDataContent());
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_non_multipart_body_is_rejected_with_problem_json()
    {
        var api = await EdApi.NewAsync(_f);
        var doc = await api.CreateDocumentAsync("Wrong content type");

        var res = await api.Http.PostAsJsonAsync($"/api/v1/documents/{doc.Id}/versions", new { file = "nope" });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_malformed_multipart_body_is_rejected_with_problem_json()
    {
        var api = await EdApi.NewAsync(_f);
        var doc = await api.CreateDocumentAsync("Malformed multipart");

        // Well-formed boundary, but a section with a junk Content-Disposition.
        var body = "--X\r\nContent-Disposition: \r\n\r\ndata\r\n--X--\r\n";
        var content = new StringContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("multipart/form-data");
        content.Headers.ContentType.Parameters.Add(new System.Net.Http.Headers.NameValueHeaderValue("boundary", "X"));

        var res = await api.Http.PostAsync($"/api/v1/documents/{doc.Id}/versions", content);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
    }

    // "Local upload only" is a negative requirement: the v1 surface must not offer a cloud ingest path.
    [Fact]
    public async Task No_cloud_connection_or_pre_signed_ingest_endpoints_exist()
    {
        var api = await EdApi.NewAsync(_f);
        var doc = await api.Http.GetStringAsync("/openapi/v1.json");

        foreach (var banned in new[] { "cloud-connections", "cloud_connections", "upload_url", "uploadUrl", ":initiate", "webdav" })
            Assert.DoesNotContain(banned, doc, StringComparison.OrdinalIgnoreCase);
    }
}
