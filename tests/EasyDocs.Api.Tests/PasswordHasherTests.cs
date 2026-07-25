using EasyDocs.Api.Auth;

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
}
