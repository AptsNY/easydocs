using System.Text.Json;
using EasyDocs.Api.Domain;

namespace EasyDocs.Api.Common;

// Append-only audit trail (spec §11): every mutation, plus public share-link reads. Reads are NOT
// audited otherwise — write-amplification for little value (documented ponytail cut, spec §11).
//
// Returns the entity rather than saving it, so callers enlist it in their own SaveChangesAsync and the
// audit row commits in the same transaction as the mutation it describes. An audit trail that can
// disagree with the change it records is worse than none.
public static class Audit
{
    public static AuditEvent Event(
        Guid orgId, Guid? documentId, Guid? actorUserId, string action,
        string? targetType = null, string? targetId = null, object? metadata = null) => new()
    {
        OrgId = orgId,
        DocumentId = documentId,
        ActorUserId = actorUserId,
        Action = action,
        TargetType = targetType,
        TargetId = targetId,
        Metadata = metadata is null ? null : JsonSerializer.Serialize(metadata),
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
