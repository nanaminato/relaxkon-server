using RelaxKon.Auth.Models.Config;
using RelaxKon.Auth.Security;

namespace RelaxKon.Auth.Tests;

public class JwtConfigurationTests
{
    [TestCase("")]
    [TestCase("short")]
    [TestCase("Set-a-long-random-secret-through-environment-specific-configuration")]
    [TestCase("long-enough-but-still-replace-me-not-a-real-secret")]
    public void Rejects_empty_short_and_example_secrets(string secret)
    {
        Assert.That(JwtConfigurationValidator.HasSafeSecrets(new JwtSettings
        { SecretKey = secret, RefreshTokenPepper = new string('b', 48) }), Is.False);
    }

    [Test]
    public void Requires_distinct_secrets()
    {
        Assert.That(JwtConfigurationValidator.HasSafeSecrets(new JwtSettings
        { SecretKey = new string('a', 48), RefreshTokenPepper = new string('a', 48) }), Is.False);
        Assert.That(JwtConfigurationValidator.HasSafeSecrets(new JwtSettings
        { SecretKey = new string('a', 48), RefreshTokenPepper = new string('b', 48) }), Is.True);
    }
}
