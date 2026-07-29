using System.Buffers.Text;
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

    // Cursor = base64url(8-byte UtcTicks || 16-byte Guid). Opaque to clients.
    public static string Encode((DateTimeOffset Time, Guid Id) key)
    {
        Span<byte> buf = stackalloc byte[24];
        BitConverter.TryWriteBytes(buf, key.Time.UtcTicks);
        key.Id.TryWriteBytes(buf[8..]);
        return Base64Url.EncodeToString(buf);
    }

    // Malformed cursor -> null, never throws.
    public static (DateTimeOffset Time, Guid Id)? Decode(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return null;
        try
        {
            var bytes = Base64Url.DecodeFromChars(cursor);
            if (bytes.Length != 24) return null;
            var ticks = BitConverter.ToInt64(bytes, 0);
            if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks) return null;
            return (new DateTimeOffset(ticks, TimeSpan.Zero), new Guid(bytes.AsSpan(8, 16)));
        }
        catch (FormatException) { return null; }
        catch (ArgumentException) { return null; }
    }

    // Keyset page over an already-filtered query, ordered by (CreatedAt, Id). Fetches limit+1 rows to
    // detect a next page; NextCursor is the last kept row's key (null at end). `descending` flips both
    // the WHERE row-value comparison and the ORDER so encode/decode stay direction-consistent.
    public static async Task<PagedResult<T>> PageAsync<T>(
        IQueryable<T> query, string? cursor, int? limit, bool descending, CancellationToken ct)
        where T : class, IKeyed
    {
        var take = ClampLimit(limit);
        if (Decode(cursor) is { } k)
            query = descending
                ? query.Where(x => x.CreatedAt < k.Time || (x.CreatedAt == k.Time && x.Id.CompareTo(k.Id) < 0))
                : query.Where(x => x.CreatedAt > k.Time || (x.CreatedAt == k.Time && x.Id.CompareTo(k.Id) > 0));

        query = descending
            ? query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            : query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id);

        var rows = await query.Take(take + 1).ToListAsync(ct);
        if (rows.Count <= take) return new PagedResult<T>(rows, null);

        rows.RemoveAt(rows.Count - 1);
        var last = rows[^1];
        return new PagedResult<T>(rows, Encode((last.CreatedAt, last.Id)));
    }
}
