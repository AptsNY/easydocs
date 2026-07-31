using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace EasyDocs.Api.Auth;

// Argon2id at OWASP-recommended cost, stored in the standard PHC string format (spec §11):
//     $argon2id$v=19$m=19456,t=2,p=1$<base64 salt>$<base64 hash>
// The point of the format is that the cost parameters travel with the digest. Verify() re-derives using
// the m/t/p it read out of the stored string, NEVER the constants below, so raising the cost is a plain
// edit to those constants: existing rows keep verifying under the cost they were minted with and new
// logins re-derive at the new cost. With the old `{salt}.{hash}` encoding the same edit would have made
// every stored hash unverifiable and locked out every user.
// Salt and hash are unpadded standard base64, per the PHC convention.
public sealed class Argon2idPasswordHasher : IPasswordHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int MemorySize = 19456; // KiB
    private const int Iterations = 2;
    private const int Parallelism = 1;

    // Konscious implements Argon2 v1.3 (= 0x13 = 19). A digest tagged with any other version is not
    // ours to verify, so the parser requires this exact string rather than accepting any v=.
    private const string Version = "v=19";

    // M0-era `{base64 salt}.{base64 hash}` rows carry no parameters, so they can only be verified
    // against the constants that produced them. Frozen: retuning these would break exactly the rows
    // they exist to keep readable. Delete the legacy branch once no install predates PHC.
    private const int LegacyMemorySize = 19456;
    private const int LegacyIterations = 2;
    private const int LegacyParallelism = 1;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Derive(password, salt, MemorySize, Iterations, Parallelism);
        return $"$argon2id${Version}$m={MemorySize},t={Iterations},p={Parallelism}${B64(salt)}${B64(hash)}";
    }

    public bool Verify(string password, string stored)
    {
        // Fail closed on anything unparseable: a corrupt PasswordHash column must not 500 /auth/login.
        if (!TryParse(stored, out var salt, out var expected, out var m, out var t, out var p))
            return false;

        // Length mismatch (a truncated column) just loses the compare — FixedTimeEquals returns false
        // rather than throwing, so there is no need to derive `expected.Length` bytes.
        var actual = Derive(password, salt, m, t, p);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static bool TryParse(
        string stored, out byte[] salt, out byte[] hash, out int m, out int t, out int p)
    {
        salt = hash = [];
        (m, t, p) = (0, 0, 0);

        if (!stored.StartsWith('$')) // legacy {salt}.{hash}; verified with the frozen M0 constants
        {
            (m, t, p) = (LegacyMemorySize, LegacyIterations, LegacyParallelism);
            return stored.Split('.') is [var ls, var lh] && TryB64(ls, out salt) && TryB64(lh, out hash);
        }

        return stored.Split('$') is ["", "argon2id", Version, var cost, var s, var h]
            && TryCost(cost, out m, out t, out p)
            && TryB64(s, out salt)
            && TryB64(h, out hash);
    }

    private static bool TryCost(string cost, out int m, out int t, out int p)
    {
        (m, t, p) = (0, 0, 0);
        return cost.Split(',') is [var ms, var ts, var ps]
            && TryInt(ms, "m=", out m) && TryInt(ts, "t=", out t) && TryInt(ps, "p=", out p)
            // Bound the cost read back from the database: an absurd m would otherwise let one corrupt
            // row OOM the process on a login attempt. Upper bound is 1 GiB; m >= 8p is Argon2's own rule.
            && m is >= 8 and <= 1_048_576 && t is >= 1 and <= 16 && p is >= 1 and <= 16 && m >= 8 * p;
    }

    private static bool TryInt(string field, string prefix, out int value)
    {
        value = 0;
        return field.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(field[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static bool TryB64(string s, out byte[] bytes)
    {
        bytes = [];
        if (s.Length is 0) return false;
        try
        {
            bytes = Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
        }
        catch (FormatException)
        {
            return false;
        }
        return bytes.Length > 0;
    }

    private static byte[] Derive(string password, byte[] salt, int memorySize, int iterations, int parallelism)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = parallelism,
            MemorySize = memorySize,
            Iterations = iterations,
        };
        return argon2.GetBytes(HashSize);
    }
}
