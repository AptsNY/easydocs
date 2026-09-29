using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyDocs.Api.Common;

// Npgsql's `timestamptz` mapping only accepts a UTC (offset 0) DateTimeOffset - any other offset
// throws ArgumentException deep inside SaveChangesAsync (a bare 500). But +02:00/-05:00/Z are all
// perfectly valid RFC 3339 offsets a conforming client is entitled to send (the OpenAPI document
// advertises these fields as `date-time`), so this converter normalizes every inbound
// DateTimeOffset to its UTC instant at deserialization time - before it reaches a handler or the
// database - rather than rejecting valid input.
//
// A value with no offset at all ("2026-08-05", a bare due *date*) is deliberately treated as
// midnight UTC via DateTimeStyles.AssumeUniversal - NOT via System.Text.Json's own default, which
// assumes the *parsing machine's local timezone*. That default would make the same request body
// resolve to a different stored instant depending on which server happened to handle it: wrong for
// a public API meant to be driven unattended from anywhere.
//
// Registered for non-nullable DateTimeOffset only: System.Text.Json wraps a struct converter for
// Nullable<T> automatically, so the three DateTimeOffset? request fields that exist today
// (ApprovalEndpoints.RequestBody.DueAt, ShareEndpoints.CreateShareLinkRequest.ExpiresAt, TokenEndpoints.CreateTokenRequest.ExpiresAt) -
// and any future one - get it for free.
public sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
            throw new JsonException($"'{s}' is not a valid date/time.");
        return value;
    }

    // The DB (and every read model backed by it) only ever holds UTC already, so this is a no-op
    // in practice - kept for symmetry, and so a value echoed straight back before a save (e.g. a
    // Created response built from the just-bound request object) is UTC too.
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime());
}
