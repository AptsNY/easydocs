using System.Net;
using EasyDocs.Api.Tests.Fixtures;

namespace EasyDocs.Api.Tests;

// One multipart call that creates a document and its first version (spec: import-document-as-new).
// Separate from DocumentUploadTests, which is about adding versions to a document that already exists.
public class DocumentImportTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public DocumentImportTests(ApiFactory f) => _f = f;

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
}
