namespace EasyDocs.Api.Domain;

// A row IS the queue (issue #16): inserted inside the same transaction as the domain write that
// needs it, claimed by workers with FOR UPDATE SKIP LOCKED, deleted on success. Survives restarts
// by construction — there is no in-memory state to lose.
public class BackgroundJob
{
    public long Id { get; set; }
    public string Type { get; set; } = null!;    // "diff" | "pdf" — one worker drains each
    public string Payload { get; set; } = null!; // JSON, shape owned by that worker
    public int Attempts { get; set; }
    public DateTimeOffset RunAfter { get; set; } // claim lease + retry backoff live here
    public DateTimeOffset CreatedAt { get; set; }
}
