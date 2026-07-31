using System.Security.Cryptography;
using System.Text;
using EasyDocs.Api.Auth;
using Konscious.Security.Cryptography;

public class PasswordHasherTests
{
    private readonly IPasswordHasher _h = new Argon2idPasswordHasher();

    [Fact]
    public void Verify_true_for_correct_password_false_otherwise()
    {
        var hash = _h.Hash("correct horse battery staple");
        Assert.NotEqual("correct horse battery staple", hash); // never plaintext
        Assert.True(_h.Verify("correct horse battery staple", hash));
        Assert.False(_h.Verify("wrong password", hash));
    }

    [Fact]
    public void Two_hashes_of_same_password_differ() // random salt
        => Assert.NotEqual(_h.Hash("same"), _h.Hash("same"));

    [Fact]
    public void Hash_is_phc_format() // self-describing: cost params travel with the digest (spec §11)
    {
        var hash = _h.Hash("pw");
        Assert.StartsWith("$argon2id$v=19$m=", hash);
        Assert.Equal(6, hash.Split('$').Length); // $argon2id$v=..$m=..,t=..,p=..$salt$hash
        Assert.DoesNotContain("=", hash.Split('$')[4]); // unpadded base64, per PHC convention
        Assert.True(_h.Verify("pw", hash));
    }

    // THE POINT OF THE FORMAT: a hash minted under one cost setting must stay verifiable after the
    // hasher's own parameters are tuned. Both PHC strings below carry the SAME salt and the SAME digest
    // (derived at m=8192,t=1); only the advertised parameters differ. Verify can only get one true and
    // one false if it re-derives with the parameters it read out of the string.
    [Fact]
    public void Verify_honours_the_parameters_embedded_in_the_hash()
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var digest = Derive("pw", salt, m: 8192, t: 1, p: 1); // not the hasher's current defaults

        Assert.True(_h.Verify("pw", Phc(8192, 1, 1, salt, digest)));   // honest label -> verifies
        Assert.False(_h.Verify("pw", Phc(19456, 2, 1, salt, digest))); // current defaults -> different digest
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("$argon2id$v=19$m=19456,t=2,p=1$only-four-fields")]
    [InlineData("$argon2i$v=19$m=19456,t=2,p=1$c2FsdHNhbHRzYWx0c2E$aGFzaA")]  // wrong variant
    [InlineData("$argon2id$v=19$m=nope,t=2,p=1$c2FsdHNhbHRzYWx0c2E$aGFzaA")]  // unparseable cost
    [InlineData("$argon2id$v=19$m=19456,t=2,p=1$!!!not-base64!!!$aGFzaA")]
    [InlineData("$argon2id$v=19$m=19456,t=2,p=1$$aGFzaA")]                    // empty salt
    public void Malformed_hash_returns_false_without_throwing(string stored) // a corrupt column must not 500 /login
        => Assert.False(_h.Verify("pw", stored));

    // Legacy M0 `{salt}.{hash}` rows cannot self-describe, so they are verified against the M0-era
    // constants held in the hasher as legacy values. Kept so anyone running from main keeps their login.
    [Fact]
    public void Legacy_dot_format_still_verifies()
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var legacy = $"{Convert.ToBase64String(salt)}.{Convert.ToBase64String(Derive("pw", salt, 19456, 2, 1))}";

        Assert.True(_h.Verify("pw", legacy));
        Assert.False(_h.Verify("wrong", legacy));
    }

    private static byte[] Derive(string password, byte[] salt, int m, int t, int p)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt, MemorySize = m, Iterations = t, DegreeOfParallelism = p,
        };
        return argon2.GetBytes(32);
    }

    private static string Phc(int m, int t, int p, byte[] salt, byte[] hash) =>
        $"$argon2id$v=19$m={m},t={t},p={p}${B64(salt)}${B64(hash)}";

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=');
}
