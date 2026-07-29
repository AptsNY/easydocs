using System.Net;
using System.Net.Http.Json;
using EasyDocs.Api.Tests;

// GET /api/v1/versions/{vid} — the version-detail endpoint from spec §10.1.
public class VersionDetailTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public VersionDetailTests(ApiFactory f) => _f = f;

    private record VersionDto(
        Guid Id, Guid DocumentId, int Major, int Minor, int Revision,
        string? Name, string Source, bool HasPdf, Guid CreatedBy);

    [Fact]
    public async Task Returns_version_metadata_for_a_member()
    {
        var a = await _f.RegisterAsync();
        var docId = await a.Client.CreateDocAsync();
        var (vid, number) = await a.Client.UploadAsync(docId);

        var res = await a.Client.GetAsync($"/api/v1/versions/{vid}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var dto = (await res.Content.ReadFromJsonAsync<VersionDto>())!;
        Assert.Equal(vid, dto.Id);
        Assert.Equal(docId, dto.DocumentId);
        Assert.Equal("0.0.1", $"{dto.Major}.{dto.Minor}.{dto.Revision}");
        Assert.Equal(number, $"{dto.Major}.{dto.Minor}.{dto.Revision}");
        Assert.Equal("Upload", dto.Source);
        Assert.False(dto.HasPdf);
        Assert.Equal(a.UserId, dto.CreatedBy);
    }

    [Fact]
    public async Task Unknown_version_is_404()
    {
        var a = await _f.RegisterAsync();
        var res = await a.Client.GetAsync($"/api/v1/versions/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Cross_org_caller_gets_404()
    {
        var a = await _f.RegisterAsync();
        var docId = await a.Client.CreateDocAsync();
        var (vid, _) = await a.Client.UploadAsync(docId);

        var b = await _f.RegisterAsync(); // own org
        var res = await b.Client.GetAsync($"/api/v1/versions/{vid}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
