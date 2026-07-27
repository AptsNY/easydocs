using System.Diagnostics;
using EasyDocs.Api.Storage;

namespace EasyDocs.Api.Publishing;

// Out-of-process renderer (spec §7): docx -> PDF via `soffice --headless --convert-to pdf`, guarded by a
// hard timeout + process-tree kill + one retry. A hung/crashing/absent soffice returns null, never throws.
// No interface — a single concrete renderer.
public sealed class LibreOfficePdfRenderer(IBlobStore blobs, ILogger<LibreOfficePdfRenderer> log)
{
    private const int TimeoutSeconds = 60;

    public async Task<string?> RenderToBlobAsync(Stream docx, CancellationToken ct)
    {
        // Buffer once so the single retry can re-feed the same bytes.
        byte[] bytes;
        try
        {
            using var buf = new MemoryStream();
            await docx.CopyToAsync(buf, ct);
            bytes = buf.ToArray();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "pdf render: failed to read source docx");
            return null;
        }

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var sha = await TryRenderAsync(bytes, ct);
            if (sha is not null) return sha;
            log.LogWarning("pdf render attempt {Attempt} failed", attempt);
        }
        return null;
    }

    private async Task<string?> TryRenderAsync(byte[] docx, CancellationToken ct)
    {
        var soffice = ResolveSoffice();
        if (soffice is null)
        {
            log.LogWarning("pdf render: no runnable soffice binary found");
            return null;
        }

        var work = Directory.CreateTempSubdirectory("edpdf").FullName;
        try
        {
            var src = Path.Combine(work, "in.docx");
            await File.WriteAllBytesAsync(src, docx, ct);

            var psi = new ProcessStartInfo(soffice)
            {
                ArgumentList = { "--headless", "--convert-to", "pdf", "--outdir", work, src },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                // Give soffice its own profile dir so a concurrent/host instance can't block or lock it.
                Environment = { ["HOME"] = work },
            };

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
            try
            {
                await proc.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(proc);
                log.LogWarning("pdf render: soffice timed out after {Seconds}s, killed", TimeoutSeconds);
                return null;
            }

            if (proc.ExitCode != 0)
            {
                log.LogWarning("pdf render: soffice exited {Code}: {Err}", proc.ExitCode, await proc.StandardError.ReadToEndAsync(ct));
                return null;
            }

            var pdf = Path.ChangeExtension(src, ".pdf");
            if (!File.Exists(pdf))
            {
                log.LogWarning("pdf render: soffice produced no output file");
                return null;
            }

            await using var stream = File.OpenRead(pdf);
            var result = await blobs.PutAsync(stream, ct);
            return result.Sha256;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "pdf render: soffice invocation failed");
            return null;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private static void TryKill(Process proc)
    {
        try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    // SOFFICE_PATH, else `soffice` on PATH, else the common macOS bundle path. Null if none is runnable.
    public static string? ResolveSoffice()
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
