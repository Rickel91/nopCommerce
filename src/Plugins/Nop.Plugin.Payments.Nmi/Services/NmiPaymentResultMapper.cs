using Nop.Core.Domain.Payments;
using Nop.Plugin.Payments.Nmi.Domain;
using Nop.Plugin.Payments.Nmi.Services.Api;
using Nop.Services.Payments;

namespace Nop.Plugin.Payments.Nmi.Services;

public static class NmiPaymentResultMapper
{
    public static ProcessPaymentResult Map(NmiApiResult<NmiTransactionResponse> apiResult,
        NmiTransactionMode mode,
        NmiPaymentMessages messages)
    {
        ArgumentNullException.ThrowIfNull(apiResult);
        ArgumentNullException.ThrowIfNull(messages);

        if (apiResult.IsAmbiguous || apiResult.Value is null && apiResult.IsSuccess)
            return Failed(messages.Ambiguous);
        if (!apiResult.IsSuccess)
            return Failed(apiResult.StatusCode == 401 ? messages.Unauthorized : messages.Failed);

        var response = apiResult.Value;
        if (response.IsDeclined)
            return Failed(messages.Declined);
        if (response.IsError)
            return Failed(response.IsAuthenticationFailure ? messages.Unauthorized : messages.Failed);
        if (!response.IsApproved || string.IsNullOrWhiteSpace(response.TransactionId))
            return Failed(messages.Ambiguous);

        // AllowStoringCreditCardNumber gates nop's CardType/expiry columns. The "number" nop receives from this plugin
        // is already masked (asterisks plus last four), so enabling it exposes brand, last four and expiry only.
        var diagnostic = BuildDiagnostic(response);
        return mode == NmiTransactionMode.Authorize
            ? new ProcessPaymentResult
            {
                AuthorizationTransactionId = response.TransactionId,
                AuthorizationTransactionCode = response.AuthorizationCode,
                AuthorizationTransactionResult = diagnostic,
                NewPaymentStatus = PaymentStatus.Authorized,
                AllowStoringCreditCardNumber = true
            }
            : new ProcessPaymentResult
            {
                CaptureTransactionId = response.TransactionId,
                CaptureTransactionResult = diagnostic,
                NewPaymentStatus = PaymentStatus.Paid,
                AllowStoringCreditCardNumber = true
            };
    }

    public static string BuildDiagnostic(NmiTransactionResponse response) =>
        $"response={Safe(response?.Response)}; response_code={Safe(response?.ResponseCode)}; " +
        $"response_text={Safe(Truncate(response?.ResponseText, 120))}; " +
        $"avs_result={NmiRedactor.Redact(NmiResponseCodes.Format(response?.AvsResponse, NmiResponseCodes.DescribeAvs))}; " +
        $"cvv_result={NmiRedactor.Redact(NmiResponseCodes.Format(response?.CvvResponse, NmiResponseCodes.DescribeCvv))}" +
        NmiResponseCodes.FormatKount(response?.KountScore);

    private static ProcessPaymentResult Failed(string message) => new() { Errors = new List<string> { message } };

    private static string Truncate(string value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];

    private static string Safe(string value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : NmiRedactor.Redact(value.Trim());
}

public record NmiPaymentMessages(string Declined, string Failed, string Unauthorized, string Ambiguous);
