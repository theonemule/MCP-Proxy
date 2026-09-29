using AIGovernanceGateway.Security;

namespace AIGovernanceGateway.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Verify_accepts_the_correct_password()
    {
        var hash = PasswordHasher.Hash("correct horse battery staple");
        Assert.True(PasswordHasher.Verify("correct horse battery staple", hash));
    }

    [Fact]
    public void Verify_rejects_an_incorrect_password()
    {
        var hash = PasswordHasher.Hash("correct horse battery staple");
        Assert.False(PasswordHasher.Verify("wrong password", hash));
    }

    [Fact]
    public void Hash_is_salted_so_repeated_hashes_differ()
    {
        var first = PasswordHasher.Hash("same-password");
        var second = PasswordHasher.Hash("same-password");
        Assert.NotEqual(first, second);
    }
}

public class ApiKeyGeneratorTests
{
    [Fact]
    public void Generated_key_verifies_against_its_own_hash()
    {
        var (prefix, plaintextKey, secretHash) = ApiKeyGenerator.Generate();
        Assert.True(ApiKeyGenerator.TrySplit(plaintextKey, out var splitPrefix, out var secret));
        Assert.Equal(prefix, splitPrefix);
        Assert.True(ApiKeyGenerator.Verify(secret, secretHash));
    }

    [Fact]
    public void Wrong_secret_fails_verification()
    {
        var (_, _, secretHash) = ApiKeyGenerator.Generate();
        Assert.False(ApiKeyGenerator.Verify("not-the-secret", secretHash));
    }

    [Fact]
    public void TrySplit_rejects_a_key_without_a_separator()
    {
        Assert.False(ApiKeyGenerator.TrySplit("no-separator-here", out _, out _));
    }
}