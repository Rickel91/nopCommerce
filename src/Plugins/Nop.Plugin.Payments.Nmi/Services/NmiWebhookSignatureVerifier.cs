using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nop.Plugin.Payments.Nmi.Services;

public static class NmiWebhookSignatureVerifier
{
    public static readonly TimeSpan DefaultFreshnessWindow = TimeSpan.FromMinutes(5);

    public static NmiWebhookSignatureVerification Verify(byte[] rawBody,
        string signatureHeader,
        IEnumerable<string> signingKeys,
        DateTimeOffset now,
        TimeSpan? freshnessWindow = null)
    {
        var result = VerifyForStores(rawBody, signatureHeader,
            signingKeys.Select(key => new NmiWebhookSigningKey(0, key)), now, freshnessWindow);
        return result.Verification;
    }

    public static NmiWebhookSignatureResult VerifyForStores(byte[] rawBody,
        string signatureHeader,
        IEnumerable<NmiWebhookSigningKey> signingKeys,
        DateTimeOffset now,
        TimeSpan? freshnessWindow = null)
    {
        if (rawBody is null || rawBody.Length == 0)
            return Result(NmiWebhookSignatureVerification.Malformed);
        if (!TryParseHeader(signatureHeader, out var timestamp, out var suppliedSignature))
            return Result(NmiWebhookSignatureVerification.Malformed);

        DateTimeOffset signedAt;
        try
        {
            signedAt = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Result(NmiWebhookSignatureVerification.Malformed);
        }

        var tolerance = freshnessWindow ?? DefaultFreshnessWindow;
        if (tolerance <= TimeSpan.Zero || (now - signedAt).Duration() > tolerance)
            return Result(NmiWebhookSignatureVerification.Expired);

        var prefix = Encoding.UTF8.GetBytes($"{timestamp}.");
        var signedPayload = new byte[prefix.Length + rawBody.Length];
        Buffer.BlockCopy(prefix, 0, signedPayload, 0, prefix.Length);
        Buffer.BlockCopy(rawBody, 0, signedPayload, prefix.Length, rawBody.Length);

        var authorizedStoreIds = new HashSet<int>();
        foreach (var key in signingKeys.Where(key => !string.IsNullOrWhiteSpace(key.Value)))
        {
            var keyBytes = Encoding.UTF8.GetBytes(key.Value);
            using var hmac = new HMACSHA256(keyBytes);
            var expected = hmac.ComputeHash(signedPayload);
            if (CryptographicOperations.FixedTimeEquals(expected, suppliedSignature))
                authorizedStoreIds.Add(key.StoreId);
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(expected);
        }

        CryptographicOperations.ZeroMemory(signedPayload);
        CryptographicOperations.ZeroMemory(suppliedSignature);
        return authorizedStoreIds.Count > 0
            ? new(NmiWebhookSignatureVerification.Valid, authorizedStoreIds)
            : Result(NmiWebhookSignatureVerification.Invalid);
    }

    private static NmiWebhookSignatureResult Result(NmiWebhookSignatureVerification verification) =>
        new(verification, new HashSet<int>());

    private static bool TryParseHeader(string header, out long timestamp, out byte[] signature)
    {
        timestamp = 0;
        signature = null;
        if (string.IsNullOrWhiteSpace(header))
            return false;

        var parts = header.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !parts[0].StartsWith("t=", StringComparison.Ordinal) ||
            !parts[1].StartsWith("s=", StringComparison.Ordinal))
            return false;

        var timestampText = parts[0][2..];
        var signatureText = parts[1][2..];
        if (timestampText.Length is < 9 or > 11 || signatureText.Length != 64 ||
            !long.TryParse(timestampText, NumberStyles.None, CultureInfo.InvariantCulture, out timestamp))
            return false;

        try
        {
            signature = Convert.FromHexString(signatureText);
            return signature.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public enum NmiWebhookSignatureVerification
{
    Valid,
    Invalid,
    Expired,
    Malformed
}

public sealed record NmiWebhookSignatureResult(NmiWebhookSignatureVerification Verification,
    IReadOnlySet<int> AuthorizedStoreIds);
