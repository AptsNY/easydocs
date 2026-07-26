using System.Threading.Channels;
using EasyDocs.Api.Events;

namespace EasyDocs.Api.Diffing;

// ponytail: Channel<DiffJob> is the queue — in-memory, recomputable on restart (spec §3/§7); no durable broker.
public record DiffJob(string FromSha, string ToSha, Guid DocumentId);

// Eager numeric-summary computation (spec §7): drains the diff queue, computes the summary in a per-job
// scope, then fans out a diff.ready SSE event.
public sealed class DiffSummaryWorker(
    ChannelReader<DiffJob> jobs, IServiceScopeFactory scopes, EventBus bus, ILogger<DiffSummaryWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in jobs.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var diff = scope.ServiceProvider.GetRequiredService<WmlComparerDiffService>();
                await diff.SummaryAsync(job.FromSha, job.ToSha, stoppingToken);
                bus.Publish(job.DocumentId, "diff.ready", new { job.FromSha, job.ToSha });
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(ex, "diff job {From}->{To} failed", job.FromSha, job.ToSha);
            }
        }
    }
}
