namespace EasyDocs.Api.Tests;

// Host LibreOffice for the merge tests' docx -> docx round trip (a stand-in for a Collabora save). The app
// itself no longer runs soffice — PDFs go through Gotenberg — and Gotenberg only outputs PDF.
// SOFFICE_PATH, else `soffice` on PATH, else the common macOS bundle path. Null if none is runnable.
public static class Soffice
{
    public static string? Resolve()
    {
        var explicitPath = Environment.GetEnvironmentVariable("SOFFICE_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return explicitPath;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, "soffice");
            if (File.Exists(candidate)) return candidate;
        }

        const string mac = "/Applications/LibreOffice.app/Contents/MacOS/soffice";
        return File.Exists(mac) ? mac : null;
    }
}
