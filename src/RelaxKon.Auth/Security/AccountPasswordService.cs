using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using RelaxKon.Auth.Models.Accounts;

namespace RelaxKon.Auth.Security;

public sealed class AccountPasswordService : IAccountPasswordService
{
    private readonly PasswordHasher<Account> _hasher = new();
    private static readonly Account LegacyWorkAccount = new();
    private static readonly string LegacyWorkHash = new PasswordHasher<Account>().HashPassword(LegacyWorkAccount, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

    public string Hash(Account account, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        return _hasher.HashPassword(account, password);
    }

    public PasswordVerificationState Verify(Account account, string password)
    {
        if (string.IsNullOrEmpty(account.Password) || string.IsNullOrEmpty(password))
        {
            return PasswordVerificationState.Invalid;
        }

        PasswordVerificationResult result;
        try
        {
            result = _hasher.VerifyHashedPassword(account, account.Password, password);
        }
        catch (FormatException)
        {
            // Legacy records were stored as plaintext and cannot be decoded as a password hash.
            result = PasswordVerificationResult.Failed;
        }
        if (result == PasswordVerificationResult.Success)
        {
            return PasswordVerificationState.Valid;
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            return PasswordVerificationState.ValidNeedsUpgrade;
        }

        // Never treat a stored Identity hash as a legacy plaintext password. Otherwise
        // submitting the database hash itself would pass the fallback comparison.
        if (IsIdentityHash(account.Password)) return PasswordVerificationState.Invalid;

        // Legacy plaintext comparisons must not provide a cheap timing oracle.
        _hasher.VerifyHashedPassword(LegacyWorkAccount, LegacyWorkHash, password);
        return MatchesLegacyPlaintext(account.Password, password)
            ? PasswordVerificationState.ValidNeedsUpgrade
            : PasswordVerificationState.Invalid;
    }

    private static bool IsIdentityHash(string value)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            // Identity V2 and V3 format markers. Reject malformed/truncated hashes too.
            return bytes.Length > 0 && bytes[0] is 0 or 1;
        }
        catch (FormatException) { return false; }
    }

    private static bool MatchesLegacyPlaintext(string persistedPassword, string suppliedPassword)
    {
        var persistedBytes = Encoding.UTF8.GetBytes(persistedPassword);
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedPassword);

        return persistedBytes.Length == suppliedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(persistedBytes, suppliedBytes);
    }
}
