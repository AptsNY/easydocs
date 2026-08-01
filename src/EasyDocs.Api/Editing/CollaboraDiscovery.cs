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

        // coolwsd builds urlsrc from the host IT was reached on — which is COLLABORA_URL, the
        // compose-internal name (`http://collabora:9980`). That name resolves inside the Docker network
        // and nowhere else, so handing it straight to a person's browser produced an editor pane that
        // could never load: DNS fails before a single request is made. The two audiences are different
        // and need different URLs — the app talks to Collabora server-to-server, the browser talks to it
        // over the published port — so re-origin the value onto the browser-facing base.
        // Defaults to COLLABORA_URL, which is exactly right when one hostname serves both (a reverse
        // proxy in front of the stack).
        var publicBase = cfg["COLLABORA_PUBLIC_URL"] is { Length: > 0 } p ? p : baseUrl;

        _cached = Rebase(urlsrc, publicBase);
        _fetchedAt = DateTimeOffset.UtcNow;
        return _cached;
    }

    // Swaps scheme+host+port and leaves everything after the authority byte-for-byte alone. String
    // surgery rather than UriBuilder on purpose: urlsrc ends in a bare `?` that callers concatenate
    // onto, and UriBuilder drops an empty query — which would silently glue `WOPISrc=` to `cool.html`.
    private static string Rebase(string urlsrc, string publicBase)
    {
        if (!Uri.TryCreate(urlsrc, UriKind.Absolute, out var from)) return urlsrc;
        if (!Uri.TryCreate(publicBase, UriKind.Absolute, out var to)) return urlsrc;

        var origin = from.GetLeftPart(UriPartial.Authority);
        return urlsrc.StartsWith(origin, StringComparison.Ordinal)
            ? string.Concat(to.GetLeftPart(UriPartial.Authority), urlsrc.AsSpan(origin.Length))
            : urlsrc;
    }
}
