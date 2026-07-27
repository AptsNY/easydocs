using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace EasyDocs.Api.Auth;

// `ed_` personal access tokens (spec §10). Stateless: mints a raw token + its SHA-256 hash; only the
// hash is persisted. Task 2's auth handler reuses Hash() to look tokens up (constant-time compare there).
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
