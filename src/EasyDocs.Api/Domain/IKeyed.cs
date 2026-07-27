namespace EasyDocs.Api.Domain;

// Stable, unique composite sort key for keyset pagination (spec §10): (CreatedAt, Id).
public interface IKeyed
{
    Guid Id { get; }
    DateTimeOffset CreatedAt { get; }
}
