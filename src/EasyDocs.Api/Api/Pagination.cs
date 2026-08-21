using System.Buffers.Text;
using System.Text;
using EasyDocs.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Api;

// Opaque, stable keyset cursor pagination on the composite key (CreatedAt, Id) (spec §10).
public sealed record PagedResult<T>(IReadOnlyList<T> Items, string? NextCursor);

public static class Pagination
{
    public const int DefaultLimit = 25;
    public const int MaxLimit = 100;

    public static int ClampLimit(int? limit) =>
        limit is null or < 1 ? DefaultLimit : Math.Min(limit.Value, MaxLimit);

    // `order=desc` is the only recognised value; anything else keeps the ascending default.
    public static bool Descending(string? order) =>
        string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase);

    // A cursor names the column its key came from. Without the tag, a client that changes ?sort= while
    // holding a cursor gets a name compared against a timestamp — a silently wrong page rather than an
    // error. Layout: 1B tag || key || 16B Guid. No length field: there is exactly one variable-length
    // field, so the key is everything between the tag and the trailing Guid.
    //
    // Cursors minted by a previous release do not decode. That is deliberate: their 24-byte payload is
    // indistinguishable from a new cursor carrying a 7-byte name key ("invoice"), so honouring them
    // would misread a real page. Decode returns null for anything unusable and a null cursor means no
    // WHERE clause, so the worst case is a client's next page restarting at the top.
    // Key is a byte[], so record equality is reference equality on it.
    public sealed record CursorKey(byte Tag, byte[] Key, Guid Id);

    // Any endpoint sorting on creation time uses it, so a `created` cursor means the same thing
    // everywhere it appears.
    public const byte CreatedTag = 0;

    public static string Encode(byte tag, ReadOnlySpan<byte> key, Guid id)
    {
        var buf = new byte[1 + key.Length + 16];
        buf[0] = tag;
        key.CopyTo(buf.AsSpan(1));
        id.TryWriteBytes(buf.AsSpan(1 + key.Length));
        return Base64Url.EncodeToString(buf);
    }

    // Malformed cursor -> null, never throws.
    public static CursorKey? Decode(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return null;
        try
        {
            var b = Base64Url.DecodeFromChars(cursor);
            if (b.Length < 17) return null;
            return new CursorKey(b[0], b[1..^16], new Guid(b.AsSpan(b.Length - 16, 16)));
        }
        catch (FormatException) { return null; }
        catch (ArgumentException) { return null; }
    }

    public static string EncodeTime(byte tag, DateTimeOffset time, Guid id)
    {
        Span<byte> key = stackalloc byte[8];
        BitConverter.TryWriteBytes(key, time.UtcTicks);
        return Encode(tag, key, id);
    }

    // Null for a key that is not a timestamp at all — a name cursor replayed on a time sort reaches here
    // only if the tag check upstream was skipped, and must not become a garbage DateTimeOffset.
    public static DateTimeOffset? AsTime(CursorKey key)
    {
        if (key.Key.Length != 8) return null;
        var ticks = BitConverter.ToInt64(key.Key, 0);
        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks) return null;
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    public static string EncodeText(byte tag, string text, Guid id) =>
        Encode(tag, Encoding.UTF8.GetBytes(text), id);

    public static string AsText(CursorKey key) => Encoding.UTF8.GetString(key.Key);

    // Keyset page over an already-filtered query, ordered by (CreatedAt, Id). Fetches limit+1 rows to
    // detect a next page; NextCursor is the last kept row's key (null at end). `descending` flips both
    // the WHERE row-value comparison and the ORDER so encode/decode stay direction-consistent.
    public static async Task<PagedResult<T>> PageAsync<T>(
        IQueryable<T> query, string? cursor, int? limit, bool descending, CancellationToken ct)
        where T : class, IKeyed
    {
        var take = ClampLimit(limit);
        if (Decode(cursor) is { Tag: CreatedTag } c && AsTime(c) is { } t)
            query = descending
                ? query.Where(x => x.CreatedAt < t || (x.CreatedAt == t && x.Id.CompareTo(c.Id) < 0))
                : query.Where(x => x.CreatedAt > t || (x.CreatedAt == t && x.Id.CompareTo(c.Id) > 0));

        query = descending
            ? query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            : query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id);

        var rows = await query.Take(take + 1).ToListAsync(ct);
        if (rows.Count <= take) return new PagedResult<T>(rows, null);

        rows.RemoveAt(rows.Count - 1);
        var last = rows[^1];
        return new PagedResult<T>(rows, EncodeTime(CreatedTag, last.CreatedAt, last.Id));
    }
}
