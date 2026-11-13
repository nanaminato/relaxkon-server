using System.Buffers.Binary;
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
        if (string.IsNullOrEmpty(password)) return PasswordVerificationState.Invalid;
        if (string.IsNullOrEmpty(account.Password))
        {
            // Pending-activation accounts must not stand out from unknown accounts by
            // skipping the same PBKDF2 work used by the controller's dummy account.
            _hasher.VerifyHashedPassword(LegacyWorkAccount, LegacyWorkHash, password);
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
        if (IsIdentityHash(account.Password))
        {
            // Structurally invalid Identity hashes fail before PBKDF2 inside Identity.
            if (!HasPbkdf2Payload(account.Password))
                _hasher.VerifyHashedPassword(LegacyWorkAccount, LegacyWorkHash, password);
            return PasswordVerificationState.Invalid;
        }

        // Legacy plaintext comparisons must not provide a cheap timing oracle.
        _hasher.VerifyHashedPassword(LegacyWorkAccount, LegacyWorkHash, password);
        return MatchesLegacyPlaintext(account.Password, password)
            ? PasswordVerificationState.ValidNeedsUpgrade
            : PasswordVerificationState.Invalid;
    }

    private static bool HasPbkdf2Payload(string value)
    {
        var bytes = Convert.FromBase64String(value);
        if (bytes[0] == 0) return bytes.Length == 49;
        if (bytes.Length < 45) return false;
        var prf = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1, 4));
        var iterations = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5, 4));
        var saltLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(9, 4));
        return prf <= 2 && iterations > 0 && iterations <= int.MaxValue && saltLength >= 16
            && saltLength <= bytes.Length - 13 - 16;
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
