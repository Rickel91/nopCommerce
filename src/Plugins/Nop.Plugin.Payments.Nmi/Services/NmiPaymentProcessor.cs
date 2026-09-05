using Microsoft.AspNetCore.Http;
using Nop.Core.Domain.Customers;
using Nop.Plugin.Payments.Nmi.Services.Api;
using Nop.Services.Common;
using Nop.Services.Customers;
using Nop.Services.Localization;
using Nop.Services.Payments;

namespace Nop.Plugin.Payments.Nmi.Services;

public class NmiPaymentProcessor
{
    #region Fields

    private readonly ICustomerService _customerService;
    private readonly IGenericAttributeService _genericAttributeService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILocalizationService _localizationService;
    private readonly NmiLogger _log;
    private readonly NmiHttpClient _httpClient;
    private readonly NmiPaymentExecutionGuard _executionGuard;
    private readonly NmiPaymentRequestBuilder _requestBuilder;

    #endregion

    #region Ctor

    public NmiPaymentProcessor(ICustomerService customerService,
        IGenericAttributeService genericAttributeService,
        IHttpContextAccessor httpContextAccessor,
        ILocalizationService localizationService,
        NmiLogger log,
        NmiHttpClient httpClient,
        NmiPaymentExecutionGuard executionGuard,
        NmiPaymentRequestBuilder requestBuilder)
    {
        _customerService = customerService;
        _genericAttributeService = genericAttributeService;
        _httpContextAccessor = httpContextAccessor;
        _localizationService = localizationService;
        _log = log ?? NmiLogger.None;
        _httpClient = httpClient;
        _executionGuard = executionGuard;
        _requestBuilder = requestBuilder;
    }

    #endregion

    #region Methods

    public async Task<ProcessPaymentResult> ProcessAsync(NmiSettings settings, ProcessPaymentRequest paymentRequest)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(paymentRequest);

        var correlation = paymentRequest.OrderGuid.ToString("N");
        var messages = await GetMessagesAsync();

        var customer = await _customerService.GetCustomerByIdAsync(paymentRequest.CustomerId);
        if (customer is null)
        {
            await _log.WarningAsync(
                $"payment rejected before gateway call; correlation={correlation}; customer={paymentRequest.CustomerId}; reason=customer_not_found.");
            return new ProcessPaymentResult { Errors = new List<string> { messages.Failed } };
        }

        var token = await GetPaymentTokenAsync(customer, paymentRequest.StoreId);
        if (token is null)
        {
            await _log.WarningAsync(
                $"payment rejected before gateway call; correlation={correlation}; store={paymentRequest.StoreId}; " +
                "reason=missing_or_invalid_payment_token. The shopper must re-enter card details.", customer: customer);
            return new ProcessPaymentResult { Errors = new List<string> { await ResourceAsync("Processing.MissingToken") } };
        }

        if (!_executionGuard.TryAcquire(paymentRequest.OrderGuid, out var executionLease))
        {
            await _log.WarningAsync(
                $"payment rejected before gateway call; correlation={correlation}; store={paymentRequest.StoreId}; " +
                "reason=duplicate_submission_in_progress.", customer: customer);
            return new ProcessPaymentResult { Errors = new List<string> { await ResourceAsync("Processing.InProgress") } };
        }

        using (executionLease)
            return await ProcessAcquiredAsync(settings, paymentRequest, customer, token, messages, correlation);
    }

    #endregion

    #region Utilities

    /// <summary>
    /// Reads the single-use token parked on the customer by <see cref="NmiPaymentMethod.GetPaymentInfoAsync"/>.
    /// </summary>
    private async Task<string> GetPaymentTokenAsync(Customer customer, int storeId)
    {
        var stored = await _genericAttributeService.GetAttributeAsync<string>(customer, NmiDefaults.PaymentTokenAttribute, storeId);
        var candidate = stored?.Trim();
        return NmiPaymentMethod.IsValidPaymentToken(candidate) ? candidate : null;
    }

    private async Task<ProcessPaymentResult> ProcessAcquiredAsync(NmiSettings settings,
        ProcessPaymentRequest paymentRequest,
        Customer customer,
        string token,
        NmiPaymentMessages messages,
        string correlation)
    {
        // Consume the token and the checkout request before the side-effecting call so confirm retries cannot reuse them.
        await _genericAttributeService.SaveAttributeAsync<string>(customer, NmiDefaults.PaymentTokenAttribute, null, paymentRequest.StoreId);
        TryConsumeCheckoutRequest();

        if (string.IsNullOrWhiteSpace(settings.EncryptedPrivateApiKey))
        {
            await _log.ErrorAsync(
                $"payment rejected before gateway call; correlation={correlation}; store={paymentRequest.StoreId}; " +
                "reason=private_api_key_not_configured. The NMI payment method is active but has no private API security key.",
                customer: customer);
            return new ProcessPaymentResult { Errors = new List<string> { await ResourceAsync("Processing.NotConfigured") } };
        }

        NmiPaymentRequest request;
        try
        {
            request = await _requestBuilder.BuildAsync(paymentRequest, settings, customer, token);
        }
        catch (Exception exception)
        {
            await _log.WarningAsync(
                $"payment request could not be built; correlation={correlation}; store={paymentRequest.StoreId}; " +
                $"order_total={paymentRequest.OrderTotal:0.00}; error_type={exception.GetType().Name}.",
                exception, customer);
            return new ProcessPaymentResult { Errors = new List<string> { messages.Failed } };
        }

        await _log.DebugAsync(settings,
            $"sending {request.TransactionType}; correlation={correlation}; store={paymentRequest.StoreId}; " +
            $"gateway={SafeHost(settings)}; amount={NmiFormat.Amount(request.Amount)}; currency={request.Currency}; " +
            $"processor_id={request.ProcessorId ?? "default"}; dup_seconds={request.DuplicateCheckSeconds?.ToString() ?? "default"}; " +
            $"billing_address={(request.BillingAddress is null ? "absent" : "present")}; " +
            $"shipping_address={(request.ShippingAddress is null ? "absent" : "present")}.",
            customer);

        try
        {
            var apiResult = await _httpClient.ProcessPaymentAsync(settings, request);
            var result = NmiPaymentResultMapper.Map(apiResult, settings.TransactionMode, messages);
            await LogResultAsync(settings, request, customer, correlation, apiResult, result);
            return result;
        }
        catch (Exception exception)
        {
            await _log.ErrorAsync(
                $"payment outcome is AMBIGUOUS; correlation={correlation}; store={paymentRequest.StoreId}; " +
                $"type={request.TransactionType}; amount={NmiFormat.Amount(request.Amount)} {request.Currency}; " +
                $"error_type={exception.GetType().Name}. Reconcile this order against the NMI merchant portal before retrying.",
                exception, customer);
            return new ProcessPaymentResult { Errors = new List<string> { messages.Ambiguous } };
        }
    }

    private void TryConsumeCheckoutRequest()
    {
        try
        {
            var session = _httpContextAccessor?.HttpContext?.Session;
            if (session is { IsAvailable: true })
                session.Remove(NmiDefaults.CheckoutPaymentInfoSessionKey);
        }
        catch
        {
            // Session access is best effort; the gateway rejects a reused token regardless.
        }
    }

    private async Task LogResultAsync(NmiSettings settings,
        NmiPaymentRequest request,
        Customer customer,
        string correlation,
        NmiApiResult<NmiTransactionResponse> apiResult,
        ProcessPaymentResult result)
    {
        var response = apiResult.Value;
        var summary = $"{request.TransactionType} result; correlation={correlation}; " +
                      $"amount={NmiFormat.Amount(request.Amount)} {request.Currency}; http_status={apiResult.StatusCode}; " +
                      $"elapsed_ms={apiResult.ElapsedMilliseconds}; transaction={response?.TransactionId ?? "none"}; " +
                      $"auth_code={response?.AuthorizationCode ?? "none"}; {NmiPaymentResultMapper.BuildDiagnostic(response)}; " +
                      $"error={apiResult.Error?.ErrorCode ?? "none"}";

        if (apiResult.IsAmbiguous)
        {
            await _log.ErrorAsync(
                $"payment outcome is AMBIGUOUS; {summary}; detail={apiResult.Error?.Message ?? "none"}. " +
                "Reconcile this order against the NMI merchant portal before retrying.",
                customer: customer);
        }
        else if (result.Success)
        {
            await _log.DebugAsync(settings, $"payment approved; {summary}; new_status={result.NewPaymentStatus}.", customer);
        }
        else
        {
            var reason = response is null ? "http_error" : response.IsDeclined ? "declined" :
                response.IsAuthenticationFailure ? "authentication_failed" : response.IsError ? "gateway_error" : "unverified";
            await _log.WarningAsync($"payment not approved; reason={reason}; {summary}.", customer: customer);
        }
    }

    private static string SafeHost(NmiSettings settings)
    {
        try
        {
            return NmiHttpClient.GetBaseUri(settings).Host;
        }
        catch (ArgumentException)
        {
            return "invalid";
        }
    }

    private async Task<NmiPaymentMessages> GetMessagesAsync() => new(
        await ResourceAsync("Processing.Declined"),
        await ResourceAsync("Processing.Failed"),
        await ResourceAsync("Processing.Unauthorized"),
        await ResourceAsync("Processing.Ambiguous"));

    private Task<string> ResourceAsync(string name) =>
        _localizationService.GetResourceAsync($"{NmiDefaults.LocaleResourcePrefix}.{name}");

    #endregion
}
