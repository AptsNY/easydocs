using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace EasyDocs.Api.Auth;

// `ed_` personal access tokens (spec §10). Stateless: mints a raw token + its SHA-256 hash; only the
// hash is persisted. ApiTokenAuthHandler reuses Hash() and looks the row up by hash equality in SQL —
// NOT a constant-time compare, and it does not need to be: the compared value is a SHA-256 digest of a
// 256-bit CSPRNG secret, so there is no low-entropy input for a timing oracle to walk (spec §11).
public sealed class ApiTokenService
{
    public (string Raw, string Hash) Mint()
    {
        var raw = "ed_" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)); // 256-bit, url-safe
        return (raw, Hash(raw));
    }

    public string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
