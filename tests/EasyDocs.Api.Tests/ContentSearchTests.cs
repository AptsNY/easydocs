using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using EasyDocs.Api.Documents;
using EasyDocs.Api.Tests.Fixtures;

namespace EasyDocs.Api.Tests;

public class DocxTextTests
{
    [Fact]
    public void Extracts_paragraph_text_with_boundaries()
    {
        var text = DocxText.Extract(new MemoryStream(DocxFixtures.Build("Alpha", "Bravo", "Charlie"))).Text;
        // Paragraph boundaries are newlines: structure a reader can use, not one run-on line.
        Assert.Equal("Alpha\nBravo\nCharlie", text);
    }

    // null, not "": /text answers 409 for these and 200 for a blank docx, and only the extractor can
    // tell them apart — BlobMime.Sniff defaults to docx, so it cannot.
    [Theory]
    [InlineData("%PDF-1.7 not a zip at all")]
    [InlineData("just plain text")]
    public void Non_docx_bytes_extract_to_null(string content)
        => Assert.Null(DocxText.Extract(new MemoryStream(Encoding.UTF8.GetBytes(content))).Text);

    [Fact]
    public void A_tab_stop_is_a_tab_not_a_line_break()
        => Assert.Equal("Section 1\tPage 4", DocxText.Extract(new MemoryStream(DocxFixtures.WithTab("Section 1", "Page 4"))).Text);

    [Fact]
    public void A_docx_with_no_text_extracts_to_empty_not_null()
        => Assert.Equal("", DocxText.Extract(new MemoryStream(DocxFixtures.Build())).Text);

    [Fact]
    public void Truncation_is_reported_by_the_extractor()
    {
        // Reported by the extractor because only it sees the loop's exit condition — Extract trims
        // after truncating, so the returned length is not a reliable substitute in general.
        var big = DocxText.Extract(new MemoryStream(DocxFixtures.Build(new string('x', DocxText.MaxChars + 1000))));
        Assert.True(big.Truncated);
        Assert.Equal(DocxText.MaxChars, big.Text!.Length); // capped, and nothing beyond the cap kept
    }
}

// Issue #12 end to end: upload a docx whose CONTENT (not name) carries a unique marker, and the
// dashboard's one search box finds it once the index worker has run. Name search must keep working
// for documents whose content says nothing.
public class ContentSearchTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private record DocDto(Guid Id);
    private record ListDto(List<DocDto> Items);

    [Fact]
    public async Task Search_finds_documents_by_content_and_still_by_name()
    {
        var client = f.CreateClient();
        var email = $"fts-{Guid.NewGuid():N}@example.com";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, displayName = "F", password = "pw-at-least-12", orgName = $"Org-{Guid.NewGuid():N}" });
        reg.EnsureSuccessStatusCode();
        var jwt = reg.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ed_session="))
            ["ed_session=".Length..].Split(';')[0];
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var marker = $"zebra{Guid.NewGuid():N}";
        var docId = (await (await client.PostAsJsonAsync("/api/v1/documents", new { name = "Boring Name" }))
            .Content.ReadFromJsonAsync<DocDto>())!.Id;
        var part = new ByteArrayContent(DocxFixtures.Build("Alpha", $"the {marker} clause", "Charlie"));
        part.Headers.ContentType = new MediaTypeHeaderValue(DocxMime);
        (await client.PostAsync($"/api/v1/documents/{docId}/versions",
            new MultipartFormDataContent { { part, "file", "d.docx" } })).EnsureSuccessStatusCode();

        // The index worker is poll-driven (Jobs:PollSeconds=1 in tests); give it a few seconds.
        var found = false;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15) && !found)
        {
            var byContent = await client.GetFromJsonAsync<ListDto>($"/api/v1/documents?q={marker}");
            found = byContent!.Items.Any(d => d.Id == docId);
            if (!found) await Task.Delay(250);
        }
        Assert.True(found, "content search never found the marker");

        var byName = await client.GetFromJsonAsync<ListDto>("/api/v1/documents?q=Boring");
        Assert.Contains(byName!.Items, d => d.Id == docId);

        var byNothing = await client.GetFromJsonAsync<ListDto>($"/api/v1/documents?q=absent{Guid.NewGuid():N}");
        Assert.DoesNotContain(byNothing!.Items, d => d.Id == docId);
    }
}
