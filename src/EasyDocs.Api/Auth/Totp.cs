using System.Security.Cryptography;
using System.Text;

namespace EasyDocs.Api.Auth;

// RFC 6238 TOTP (SHA-1, 6 digits, 30-second steps) plus the base32 the otpauth:// URI speaks.
// Hand-rolled on purpose: HMACSHA1 ships in the BCL and the whole algorithm is thirty lines — a
// dependency would be bigger than the code it replaced. SHA-1 here is the RFC's HMAC choice and
// what every authenticator app implements; it is not a collision-resistance claim.
public static class Totp
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const int StepSeconds = 30;

    public static string NewSecret() => Base32Encode(RandomNumberGenerator.GetBytes(20));

    public static string OtpauthUri(string account, string secret) =>
        $"otpauth://totp/easydocs:{Uri.EscapeDataString(account)}?secret={secret}&issuer=easydocs";

    public static string Code(string base32Secret, DateTimeOffset at)
        => Hotp(Base32Decode(base32Secret), at.ToUnixTimeSeconds() / StepSeconds);

    /// <summary>±1 step of clock skew, constant-time comparison.</summary>
    public static bool Verify(string base32Secret, string code, DateTimeOffset at)
    {
        var key = Base32Decode(base32Secret);
        var step = at.ToUnixTimeSeconds() / StepSeconds;
        var given = Encoding.ASCII.GetBytes(code.Trim());
        for (var offset = -1; offset <= 1; offset++)
        {
            var expected = Encoding.ASCII.GetBytes(Hotp(key, step + offset));
            if (given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected))
                return true;
        }
        return false;
    }

    private static string Hotp(byte[] key, long counter)
    {
        Span<byte> msg = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(msg, counter);
        var hash = HMACSHA1.HashData(key, msg);
        var o = hash[^1] & 0x0F; // RFC 4226 dynamic truncation
        var binary = ((hash[o] & 0x7F) << 24) | (hash[o + 1] << 16) | (hash[o + 2] << 8) | hash[o + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    public static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Base32Decode(string s)
    {
        var clean = s.Trim().TrimEnd('=').ToUpperInvariant();
        var bytes = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var v = Alphabet.IndexOf(c);
            if (v < 0) throw new FormatException($"'{c}' is not a base32 character.");
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return [.. bytes];
    }
}

// Login fallbacks for a lost authenticator: ten single-use codes, stored as SHA-256 hashes — a
// database leak must not hand out working codes, same rule as passwords and ed_ tokens.
public static class RecoveryCodes
{
    public static string[] Generate() =>
        [.. Enumerable.Range(0, 10).Select(_ =>
        {
            var raw = Totp.Base32Encode(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant();
            return $"{raw[..4]}-{raw[4..8]}-{raw[8..12]}";
        })];

    public static string Hash(string code) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(code.Trim().ToLowerInvariant())));
}
