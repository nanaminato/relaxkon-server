using System.Text;
using RelaxKon.Auth.Models.Config;

namespace RelaxKon.Auth.Security;

public static class JwtConfigurationValidator
{
    public static bool HasSafeSecrets(JwtSettings settings) =>
        IsSecret(settings.SecretKey) && IsSecret(settings.RefreshTokenPepper)
        && !string.Equals(settings.SecretKey, settings.RefreshTokenPepper, StringComparison.Ordinal);

    private static bool IsSecret(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Encoding.UTF8.GetByteCount(value) >= 32
        && !value.StartsWith("Set-", StringComparison.OrdinalIgnoreCase)
        && !value.Contains("replace-me", StringComparison.OrdinalIgnoreCase);
}
