using System.Text;
using EasyDocs.Api.Auth;

namespace EasyDocs.Api.Tests;

public class TotpTests
{
    // RFC 6238 Appendix B test vectors (SHA-1), truncated from 8 to our 6 digits.
    private static readonly string Rfc6238Secret =
        Totp.Base32Encode(Encoding.ASCII.GetBytes("12345678901234567890"));

    [Theory]
    [InlineData(59, "287082")]           // vector: 94287082
    [InlineData(1111111109, "081804")]   // vector: 07081804
    [InlineData(1234567890, "005924")]   // vector: 89005924
    [InlineData(20000000000, "353130")]  // vector: 65353130
    public void Matches_the_rfc_6238_vectors(long unixSeconds, string expected)
        => Assert.Equal(expected, Totp.Code(Rfc6238Secret, DateTimeOffset.FromUnixTimeSeconds(unixSeconds)));

    [Fact]
    public void Verify_accepts_one_step_of_skew_and_rejects_more()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var secret = Totp.NewSecret();
        Assert.True(Totp.Verify(secret, Totp.Code(secret, at), at));
        Assert.True(Totp.Verify(secret, Totp.Code(secret, at.AddSeconds(-30)), at));
        Assert.True(Totp.Verify(secret, Totp.Code(secret, at.AddSeconds(30)), at));
        Assert.False(Totp.Verify(secret, Totp.Code(secret, at.AddSeconds(90)), at));
        Assert.False(Totp.Verify(secret, "000000", at));
    }

    [Fact]
    public void Base32_roundtrips()
    {
        var data = System.Security.Cryptography.RandomNumberGenerator.GetBytes(20);
        Assert.Equal(data, Totp.Base32Decode(Totp.Base32Encode(data)));
    }

    [Fact]
    public void Recovery_codes_are_distinct_and_hash_case_insensitively()
    {
        var codes = RecoveryCodes.Generate();
        Assert.Equal(10, codes.Distinct().Count());
        Assert.Equal(RecoveryCodes.Hash(codes[0]), RecoveryCodes.Hash(codes[0].ToUpperInvariant()));
    }
}
