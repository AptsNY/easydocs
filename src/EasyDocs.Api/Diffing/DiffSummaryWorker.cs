using System.Threading.Channels;
using EasyDocs.Api.Common;
using EasyDocs.Api.Events;

namespace EasyDocs.Api.Diffing;

public record DiffJob(string FromSha, string ToSha, Guid DocumentId);

// Eager numeric-summary computation (spec §7): drains the durable diff queue (issue #16 — the
// BackgroundJobs table, enqueued inside the commit's transaction), computes the summary in a
// per-job scope, then fans out a diff.ready SSE event. The Channel<DiffJob> is only the wake-up
// nudge; the table is the queue.
public sealed class DiffSummaryWorker(
    ChannelReader<DiffJob> nudges, IServiceScopeFactory scopes, IConfiguration cfg,
    EventBus bus, ILogger<DiffSummaryWorker> log)
    : DurableJobWorker<DiffJob>(BackgroundJobs.Diff, scopes, cfg, log)
{
    protected override async Task AwaitNudgeAsync(CancellationToken ct) => await nudges.ReadAsync(ct);

    protected override async Task HandleAsync(IServiceProvider services, DiffJob job, CancellationToken ct)
    {
        var diff = services.GetRequiredService<WmlComparerDiffService>();
        await diff.SummaryAsync(job.FromSha, job.ToSha, ct);
        bus.Publish(job.DocumentId, "diff.ready", new { job.FromSha, job.ToSha });
    }
}
