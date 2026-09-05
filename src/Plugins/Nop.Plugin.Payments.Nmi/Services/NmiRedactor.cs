using System.Text.RegularExpressions;
using Nop.Plugin.Payments.Nmi.Services.Api;

namespace Nop.Plugin.Payments.Nmi.Services;

public static partial class NmiRedactor
{
    [GeneratedRegex("(?i)(authorization|payment_token|security_key|private_api_key|webhook_signing_key|pan|card_number|cardnumber|ccnumber|card_code|cardcode|cvv2?|cvc2?)([\\\"'=:\\s]+)([^\\\"'&,}\\s]+)")]
    private static partial Regex SensitiveValuePattern();

    [GeneratedRegex("(?<!\\d)(?:\\d[ -]?){12,18}\\d(?!\\d)")]
    private static partial Regex PrimaryAccountNumberPattern();

    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var redacted = SensitiveValuePattern().Replace(value, "$1$2[REDACTED]");
        return PrimaryAccountNumberPattern().Replace(redacted, "[REDACTED]");
    }

    public static NmiErrorResponse Redact(NmiErrorResponse error)
    {
        if (error is null)
            return null;

        return error with { Message = Redact(error.Message) };
    }
}
