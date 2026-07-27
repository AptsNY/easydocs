using System.Xml.Linq;

namespace EasyDocs.Api.Editing;

// Resolves Collabora's "edit docx" action urlsrc from its discovery XML (spec §6.1).
// COLLABORA_ACTION_URL config short-circuits live discovery (test/dev seam).
// ponytail: daily refresh via a stored timestamp, no cron/background job; a benign cold-start
// double-fetch just rewrites the same value.
public class CollaboraDiscovery(IConfiguration cfg, HttpClient http)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private string? _cached;
    private DateTimeOffset _fetchedAt;

    public async Task<string> ActionUrlForDocxAsync(CancellationToken ct)
    {
        if (cfg["COLLABORA_ACTION_URL"] is { Length: > 0 } seam) return seam;

        if (_cached is not null && DateTimeOffset.UtcNow - _fetchedAt < Ttl) return _cached;

        var baseUrl = cfg["COLLABORA_URL"] ?? throw new InvalidOperationException("COLLABORA_URL not configured");
        var xml = await http.GetStringAsync($"{baseUrl}/hosting/discovery", ct);
        var urlsrc = XDocument.Parse(xml).Descendants("action")
            .Where(a => (string?)a.Attribute("ext") == "docx" && (string?)a.Attribute("name") == "edit")
            .Select(a => (string?)a.Attribute("urlsrc"))
            .FirstOrDefault()
            ?? throw new InvalidOperationException("No docx edit action in Collabora discovery XML.");

        _cached = urlsrc;
        _fetchedAt = DateTimeOffset.UtcNow;
        return urlsrc;
    }
}
