using System.Globalization;
using Nop.Core.Domain.Orders;
using Nop.Core.Domain.Payments;
using Nop.Plugin.Payments.Nmi.Services.Api;
using Nop.Services.Localization;
using Nop.Services.Logging;
using Nop.Services.Orders;
using Nop.Services.Payments;
using Nop.Services.Stores;

namespace Nop.Plugin.Payments.Nmi.Services;

public class NmiOrderOperationService
{
    private readonly NmiHttpClient _httpClient;
    private readonly ILocalizationService _localizationService;
    private readonly NmiLogger _log;
    private readonly IOrderService _orderService;
    private readonly IStoreService _storeService;

    public NmiOrderOperationService(NmiHttpClient httpClient,
        ILocalizationService localizationService,
        NmiLogger log,
        IOrderService orderService,
        IStoreService storeService)
    {
        _httpClient = httpClient;
        _localizationService = localizationService;
        _log = log ?? NmiLogger.None;
        _orderService = orderService;
        _storeService = storeService;
    }

    /// <summary>
    /// Stamps the gateway transaction with the store order number once nop has assigned it (order description and
    /// PO number, since <c>orderid</c> is fixed at sale time to the order GUID). Never throws; failures are logged.
    /// </summary>
    public async Task StampOrderReferenceAsync(NmiSettings settings, Order order)
    {
        if (settings is null || order is null)
            return;

        var transactionId = string.IsNullOrWhiteSpace(order.CaptureTransactionId)
            ? order.AuthorizationTransactionId
            : order.CaptureTransactionId;
        var orderNumber = string.IsNullOrWhiteSpace(order.CustomOrderNumber) ? order.Id.ToString() : order.CustomOrderNumber;
        if (string.IsNullOrWhiteSpace(transactionId) || string.IsNullOrWhiteSpace(settings.EncryptedPrivateApiKey))
            return;

        try
        {
            var store = await _storeService.GetStoreByIdAsync(order.StoreId);
            var description = $"{store?.Name ?? "nopCommerce"} order #{orderNumber}";
            var result = await _httpClient.UpdateTransactionAsync(settings, transactionId, description, orderNumber);
            var response = result.Value;
            var summary = $"order={order.Id}; order_number={orderNumber}; transaction={transactionId}; " +
                          $"http_status={result.StatusCode}; elapsed_ms={result.ElapsedMilliseconds}; " +
                          $"response={response?.Response ?? "none"}; response_text={NmiRedactor.Redact(response?.ResponseText ?? result.Error?.ErrorCode ?? "none")}";
            if (result.IsSuccess && response.IsApproved)
            {
                await _log.DebugAsync(settings, $"order reference stamped on gateway transaction; {summary}.");
                await TryInsertNoteAsync(order, $"NMI transaction {transactionId} tagged with order #{orderNumber} (description and PO number).");
            }
            else
            {
                await _log.WarningAsync($"order reference could not be stamped on gateway transaction; {summary}.");
            }
        }
        catch (Exception exception)
        {
            await _log.WarningAsync(
                $"order reference could not be stamped on gateway transaction; order={order.Id}; transaction={transactionId}; " +
                $"error_type={exception.GetType().Name}.", exception);
        }
    }

    private async Task TryInsertNoteAsync(Order order, string note)
    {
        try
        {
            await _orderService.InsertOrderNoteAsync(new OrderNote
            {
                OrderId = order.Id,
                Note = note,
                DisplayToCustomer = false,
                CreatedOnUtc = DateTime.UtcNow
            });
        }
        catch (Exception exception)
        {
            await _log.WarningAsync($"order note could not be saved for order {order.Id}; error_type={exception.GetType().Name}.", exception);
        }
    }

    public async Task<CapturePaymentResult> CaptureAsync(NmiSettings settings, CapturePaymentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Order);

        var validation = NmiOrderOperationValidator.ValidateCapture(request.Order);
        if (validation != NmiOperationValidationError.None)
            return new CapturePaymentResult { Errors = new List<string> { await ValidationMessageAsync(validation) } };

        var rootId = request.Order.AuthorizationTransactionId;
        var amount = request.Order.OrderTotal;
        var orderNumber = string.IsNullOrWhiteSpace(request.Order.CustomOrderNumber)
            ? request.Order.Id.ToString()
            : request.Order.CustomOrderNumber;
        var outcome = await ExecuteAsync(settings, request.Order, NmiTransactionTypes.Capture, rootId, amount,
            () => _httpClient.CapturePaymentAsync(settings, rootId, amount, orderNumber));
        if (!outcome.Success)
            return new CapturePaymentResult { Errors = new List<string> { await OutcomeMessageAsync(outcome) } };

        return new CapturePaymentResult
        {
            CaptureTransactionId = rootId,
            CaptureTransactionResult = outcome.Diagnostic,
            NewPaymentStatus = PaymentStatus.Paid
        };
    }

    public async Task<VoidPaymentResult> VoidAsync(NmiSettings settings, VoidPaymentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Order);

        var validation = NmiOrderOperationValidator.ValidateVoid(request.Order, out var rootId);
        if (validation != NmiOperationValidationError.None)
            return new VoidPaymentResult { Errors = new List<string> { await ValidationMessageAsync(validation) } };

        var outcome = await ExecuteAsync(settings, request.Order, NmiTransactionTypes.Void, rootId, request.Order.OrderTotal,
            () => _httpClient.VoidPaymentAsync(settings, rootId));
        return outcome.Success
            ? new VoidPaymentResult { NewPaymentStatus = PaymentStatus.Voided }
            : new VoidPaymentResult { Errors = new List<string> { await OutcomeMessageAsync(outcome) } };
    }

    public async Task<RefundPaymentResult> RefundAsync(NmiSettings settings, RefundPaymentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Order);

        var validation = NmiOrderOperationValidator.ValidateRefund(request.Order, request.AmountToRefund,
            out var remainingAmount);
        if (validation != NmiOperationValidationError.None)
            return new RefundPaymentResult { Errors = new List<string> { await ValidationMessageAsync(validation) } };

        var rootId = request.Order.CaptureTransactionId;
        var outcome = await ExecuteAsync(settings, request.Order, NmiTransactionTypes.Refund, rootId, request.AmountToRefund,
            () => _httpClient.RefundPaymentAsync(settings, rootId, request.AmountToRefund));
        if (!outcome.Success)
            return new RefundPaymentResult { Errors = new List<string> { await OutcomeMessageAsync(outcome) } };

        return new RefundPaymentResult
        {
            NewPaymentStatus = request.AmountToRefund == remainingAmount
                ? PaymentStatus.Refunded
                : PaymentStatus.PartiallyRefunded
        };
    }

    private async Task<NmiOperationOutcome> ExecuteAsync(NmiSettings settings,
        Order order,
        string operation,
        string rootId,
        decimal amount,
        Func<Task<NmiApiResult<NmiTransactionResponse>>> sendAsync)
    {
        var context = $"order={order.Id}; root_transaction={Safe(rootId)}; " +
                      $"amount={amount.ToString("0.00", CultureInfo.InvariantCulture)}; payment_status={order.PaymentStatus}";
        await _log.DebugAsync(settings, $"sending {operation}; {context}.");

        NmiApiResult<NmiTransactionResponse> apiResult;
        try
        {
            apiResult = await sendAsync();
        }
        catch (Exception exception)
        {
            await _log.ErrorAsync(
                $"{operation} outcome is AMBIGUOUS; {context}; error_type={exception.GetType().Name}. " +
                "Check the transaction in the NMI merchant portal before retrying.", exception);
            return NmiOperationOutcome.AmbiguousResult;
        }

        var outcome = NmiOrderOperationMapper.Map(apiResult, operation, rootId);
        var summary = $"{operation} result; {context}; http_status={apiResult.StatusCode}; " +
                      $"elapsed_ms={apiResult.ElapsedMilliseconds}; action={Safe(outcome.ActionId)}; " +
                      $"result={outcome.Diagnostic}; error={apiResult.Error?.ErrorCode ?? "none"}";
        if (outcome.Ambiguous)
            await _log.ErrorAsync($"{operation} outcome is AMBIGUOUS; {summary}; detail={apiResult.Error?.Message ?? "none"}. " +
                                  "Check the transaction in the NMI merchant portal before retrying.");
        else if (!outcome.Success)
            await _log.WarningAsync($"{operation} not approved; reason={(outcome.Unauthorized ? "authentication_failed" : outcome.Declined ? "declined" : "gateway_error")}; {summary}.");
        else
            await _log.DebugAsync(settings, $"{operation} approved; {summary}.");

        if (outcome.Success)
            await TryAddOrderNoteAsync(order, operation, amount, outcome);
        return outcome;
    }

    private async Task TryAddOrderNoteAsync(Order order,
        string operation,
        decimal amount,
        NmiOperationOutcome outcome)
    {
        var now = DateTime.UtcNow;
        // The refund marker format is also used by the webhook processor to recognise synchronous refunds.
        var note = $"NMI {operation}; amount={amount.ToString("0.00", CultureInfo.InvariantCulture)}; " +
                   $"root_transaction={Safe(outcome.RootTransactionId)}; action={Safe(outcome.ActionId)}; " +
                   $"result={outcome.Diagnostic}; timestamp={now:O}.";
        try
        {
            await _orderService.InsertOrderNoteAsync(new OrderNote
            {
                OrderId = order.Id,
                Note = NmiRedactor.Redact(note),
                DisplayToCustomer = false,
                CreatedOnUtc = now
            });
        }
        catch (Exception exception)
        {
            await _log.WarningAsync(
                $"{operation} succeeded for order {order.Id}, but its order note could not be saved; " +
                $"error_type={exception.GetType().Name}.", exception);
        }
    }

    private Task<string> ValidationMessageAsync(NmiOperationValidationError error) =>
        ResourceAsync(error switch
        {
            NmiOperationValidationError.InvalidAmount => "Operations.InvalidAmount",
            NmiOperationValidationError.MissingTransaction => "Operations.MissingTransaction",
            _ => "Operations.InvalidState"
        });

    private Task<string> OutcomeMessageAsync(NmiOperationOutcome outcome) =>
        ResourceAsync(outcome.Ambiguous
            ? "Operations.Ambiguous"
            : outcome.Unauthorized
                ? "Operations.Unauthorized"
                : outcome.Declined
                    ? "Operations.Declined"
                    : "Operations.Failed");

    private Task<string> ResourceAsync(string name) =>
        _localizationService.GetResourceAsync($"{NmiDefaults.LocaleResourcePrefix}.{name}");

    private static string Safe(string value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
}

internal static class NmiOrderOperationValidator
{
    public static NmiOperationValidationError ValidateCapture(Order order)
    {
        if (order.PaymentStatus != PaymentStatus.Authorized || order.RefundedAmount != 0)
            return NmiOperationValidationError.InvalidState;
        if (!ValidAmount(order.OrderTotal))
            return NmiOperationValidationError.InvalidAmount;
        return ValidTransactionId(order.AuthorizationTransactionId)
            ? NmiOperationValidationError.None
            : NmiOperationValidationError.MissingTransaction;
    }

    public static NmiOperationValidationError ValidateVoid(Order order, out string rootId)
    {
        rootId = order.PaymentStatus == PaymentStatus.Authorized
            ? order.AuthorizationTransactionId
            : order.CaptureTransactionId;
        if (order.PaymentStatus is not (PaymentStatus.Authorized or PaymentStatus.Paid))
            return NmiOperationValidationError.InvalidState;
        if (!ValidAmount(order.OrderTotal))
            return NmiOperationValidationError.InvalidAmount;
        return ValidTransactionId(rootId)
            ? NmiOperationValidationError.None
            : NmiOperationValidationError.MissingTransaction;
    }

    public static NmiOperationValidationError ValidateRefund(Order order,
        decimal amount,
        out decimal remainingAmount)
    {
        remainingAmount = order.OrderTotal - order.RefundedAmount;
        if (order.PaymentStatus is not (PaymentStatus.Paid or PaymentStatus.PartiallyRefunded))
            return NmiOperationValidationError.InvalidState;
        if (!ValidAmount(amount) || !ValidAmount(remainingAmount) || amount > remainingAmount)
            return NmiOperationValidationError.InvalidAmount;
        return ValidTransactionId(order.CaptureTransactionId)
            ? NmiOperationValidationError.None
            : NmiOperationValidationError.MissingTransaction;
    }

    private static bool ValidAmount(decimal amount) =>
        amount > 0 && decimal.Round(amount, 2, MidpointRounding.AwayFromZero) == amount;

    private static bool ValidTransactionId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 100 && !value.Any(char.IsWhiteSpace);
}

internal enum NmiOperationValidationError
{
    None,
    InvalidState,
    InvalidAmount,
    MissingTransaction
}

internal static class NmiOrderOperationMapper
{
    /// <summary>
    /// Classifies a follow-on (capture/void/refund) Payment API response. Capture and void echo the original
    /// transaction id; a refund returns the id of the new refund transaction, which is kept as the action id.
    /// </summary>
    public static NmiOperationOutcome Map(NmiApiResult<NmiTransactionResponse> apiResult,
        string operation,
        string expectedRootId = null)
    {
        if (apiResult.IsAmbiguous || apiResult.Value is null && apiResult.IsSuccess)
            return NmiOperationOutcome.AmbiguousResult;
        if (!apiResult.IsSuccess)
            return new(false, false, apiResult.StatusCode == 401, false, expectedRootId, null,
                $"error={Safe(apiResult.Error?.ErrorCode)}");

        var response = apiResult.Value;
        var rootId = expectedRootId ?? response.TransactionId;
        if (response.IsDeclined)
            return new(false, false, false, true, rootId, response.TransactionId, Diagnostic(response));
        if (response.IsError)
            return new(false, false, response.IsAuthenticationFailure, false, rootId, response.TransactionId,
                Diagnostic(response));
        if (!response.IsApproved || string.IsNullOrWhiteSpace(response.TransactionId))
            return NmiOperationOutcome.AmbiguousResult;

        var actionId = string.Equals(response.TransactionId, rootId, StringComparison.Ordinal)
            ? null
            : response.TransactionId;
        return new(true, false, false, false, rootId, actionId, Diagnostic(response));
    }

    private static string Diagnostic(NmiTransactionResponse response) =>
        $"response={Safe(response.Response)}; response_code={Safe(response.ResponseCode)}; " +
        $"response_text={Safe(Truncate(response.ResponseText, 120))}";

    private static string Truncate(string value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];

    private static string Safe(string value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : NmiRedactor.Redact(value.Trim());
}

internal record NmiOperationOutcome(bool Success,
    bool Ambiguous,
    bool Unauthorized,
    bool Declined,
    string RootTransactionId,
    string ActionId,
    string Diagnostic)
{
    public static NmiOperationOutcome AmbiguousResult { get; } =
        new(false, true, false, false, null, null, "status=ambiguous");
}
