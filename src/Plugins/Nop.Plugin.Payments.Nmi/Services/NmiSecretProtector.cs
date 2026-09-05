using Nop.Services.Security;

namespace Nop.Plugin.Payments.Nmi.Services;

public static class NmiSecretProtector
{
    public static string Protect(string submitted, string current, IEncryptionService encryptionService)
    {
        ArgumentNullException.ThrowIfNull(encryptionService);
        return IsUnchanged(submitted) ? current : encryptionService.EncryptText(submitted.Trim());
    }

    /// <summary>
    /// A blank submission, the exact mask, or any value made only of mask characters (bullets or asterisks, however
    /// the browser or proxy encoded them) means "keep the stored secret".
    /// </summary>
    public static bool IsUnchanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == NmiDefaults.MaskedSecret)
            return true;

        string unescaped;
        try
        {
            unescaped = Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            unescaped = value;
        }

        return IsMaskOnly(value) || IsMaskOnly(unescaped);
    }

    private static bool IsMaskOnly(string value) =>
        value.Trim().Length > 0 &&
        value.Trim().All(character => character is '•' or '*' or '●' or '·' or '?' or '�');

    public static string Mask(string encrypted) =>
        string.IsNullOrWhiteSpace(encrypted) ? string.Empty : NmiDefaults.MaskedSecret;
}
