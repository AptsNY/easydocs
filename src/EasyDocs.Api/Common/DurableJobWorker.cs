using EasyDocs.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Common;

// Postgres-backed queue drain (issue #16). The BackgroundJobs table is the queue; the channel each
// subclass listens on is a latency nudge only — a worker that never hears the nudge still finds the
// row on its next poll, and rows left behind by a dead process are picked up the same way at boot.
//
// Claiming is one atomic UPDATE: it bumps Attempts and pushes RunAfter a lease into the future, so
// a worker that dies mid-job leaves a row that retries after the lease expires — never a job that
// runs twice concurrently, and never one that vanishes. Success deletes the row; failure leaves it
// (the bumped RunAfter is the backoff); a job that keeps failing is dropped loudly at MaxAttempts.
public abstract class DurableJobWorker<TPayload>(
    string type, IServiceScopeFactory scopes, IConfiguration cfg, ILogger log) : BackgroundService
{
    // ponytail: fixed lease/backoff, no per-job tuning — revisit if a job class ever needs more
    // than 2 minutes or five tries. Poll is configurable only as a test seam (Jobs:PollSeconds).
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private const int MaxAttempts = 5;
    private readonly TimeSpan _poll = TimeSpan.FromSeconds(cfg.GetValue("Jobs:PollSeconds", 15));

    /// Completes when an enqueuer signals new work; used only to cut idle latency between polls.
    protected abstract Task AwaitNudgeAsync(CancellationToken ct);

    /// Process one payload. Returning normally consumes the job; throwing leaves it to retry.
    protected abstract Task HandleAsync(IServiceProvider services, TPayload payload, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await RunOneAsync(stoppingToken)) continue; // drain until empty
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                idle.CancelAfter(_poll);
                try { await AwaitNudgeAsync(idle.Token); }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // The loop itself must survive anything (DB restart, transient network) — parity
                // with the old channel workers, which never let a job kill the host.
                log.LogError(ex, "{Type} job loop faulted; retrying in {Poll}", type, _poll);
                try { await Task.Delay(_poll, stoppingToken); } catch (OperationCanceledException) { }
            }
        }
    }

    private async Task<bool> RunOneAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();

        var leaseSeconds = (int)Lease.TotalSeconds;
        var job = (await db.Database.SqlQuery<ClaimedJob>($"""
            UPDATE "BackgroundJobs" AS b
            SET "Attempts" = b."Attempts" + 1,
                "RunAfter" = now() + make_interval(secs => {leaseSeconds})
            WHERE b."Id" = (SELECT "Id" FROM "BackgroundJobs"
                            WHERE "Type" = {type} AND "RunAfter" <= now()
                            ORDER BY "Id"
                            LIMIT 1
                            FOR UPDATE SKIP LOCKED)
            RETURNING b."Id", b."Payload", b."Attempts"
            """).ToListAsync(ct)).SingleOrDefault();
        if (job is null) return false;

        if (job.Attempts > MaxAttempts)
        {
            // Dropped loudly, not silently: the payload is in the log, so the job can be re-run by
            // hand (a PDF by re-publishing, a diff by requesting the comparison) once the cause is fixed.
            log.LogError("{Type} job {Id} exceeded {Max} attempts; dropping. Payload: {Payload}",
                type, job.Id, MaxAttempts, job.Payload);
            await db.BackgroundJobs.Where(j => j.Id == job.Id).ExecuteDeleteAsync(ct);
            return true;
        }

        try
        {
            var payload = System.Text.Json.JsonSerializer.Deserialize<TPayload>(job.Payload)
                ?? throw new InvalidOperationException($"payload deserialized to null: {job.Payload}");
            await HandleAsync(scope.ServiceProvider, payload, ct);
            await db.BackgroundJobs.Where(j => j.Id == job.Id).ExecuteDeleteAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Leave the row: the claim already pushed RunAfter out, which is the retry backoff.
            log.LogError(ex, "{Type} job {Id} failed (attempt {Attempt}/{Max}); will retry",
                type, job.Id, job.Attempts, MaxAttempts);
        }
        return true;
    }

    private sealed class ClaimedJob
    {
        public long Id { get; set; }
        public string Payload { get; set; } = null!;
        public int Attempts { get; set; }
    }
}

// The enqueue half: build the row that DurableJobWorker's subclasses will claim. Add it to the
// SAME DbContext/transaction as the domain change that needs the job — that is the durability
// contract: the job exists iff the work committed.
public static class BackgroundJobs
{
    public const string Diff = "diff";
    public const string Pdf = "pdf";

    public static Domain.BackgroundJob For<TPayload>(string type, TPayload payload) => new()
    {
        Type = type,
        Payload = System.Text.Json.JsonSerializer.Serialize(payload),
        RunAfter = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
