using Microsoft.AspNetCore.Http;
using Nop.Core;
using Nop.Core.Domain.Orders;
using Nop.Core.Domain.Payments;
using Nop.Core.Domain.ScheduleTasks;
using Nop.Plugin.Payments.Nmi.Components;
using Nop.Plugin.Payments.Nmi.Services;
using Nop.Services.Common;
using Nop.Services.Configuration;
using Nop.Services.Localization;
using Nop.Services.Orders;
using Nop.Services.Payments;
using Nop.Services.Plugins;
using Nop.Services.ScheduleTasks;

namespace Nop.Plugin.Payments.Nmi;

/// <summary>
/// Represents the NMI payment method (Collect.js tokenization + Payment API).
/// </summary>
public class NmiPaymentMethod : BasePlugin, IPaymentMethod
{
    private readonly IGenericAttributeService _genericAttributeService;
    private readonly ILocalizationService _localizationService;
    private readonly ISettingService _settingService;
    private readonly IOrderTotalCalculationService _orderTotalCalculationService;
    private readonly IStoreContext _storeContext;
    private readonly IWebHelper _webHelper;
    private readonly IWorkContext _workContext;
    private readonly PaymentSettings _paymentSettings;
    private readonly NmiPaymentProcessor _paymentProcessor;
    private readonly NmiOrderOperationService _orderOperationService;
    private readonly IScheduleTaskService _scheduleTaskService;

    public NmiPaymentMethod(
        IGenericAttributeService genericAttributeService,
        ILocalizationService localizationService,
        IOrderTotalCalculationService orderTotalCalculationService,
        ISettingService settingService,
        IStoreContext storeContext,
        IWebHelper webHelper,
        IWorkContext workContext,
        NmiSettings nmiSettings,
        PaymentSettings paymentSettings,
        NmiPaymentProcessor paymentProcessor,
        NmiOrderOperationService orderOperationService,
        IScheduleTaskService scheduleTaskService)
    {
        _genericAttributeService = genericAttributeService;
        _localizationService = localizationService;
        _orderTotalCalculationService = orderTotalCalculationService;
        _settingService = settingService;
        _storeContext = storeContext;
        _webHelper = webHelper;
        _workContext = workContext;
        Settings = nmiSettings;
        _paymentSettings = paymentSettings;
        _paymentProcessor = paymentProcessor;
        _orderOperationService = orderOperationService;
        _scheduleTaskService = scheduleTaskService;
    }

    private NmiSettings Settings { get; }

    /// <inheritdoc />
    public Task<ProcessPaymentResult> ProcessPaymentAsync(ProcessPaymentRequest processPaymentRequest)
    {
        ArgumentNullException.ThrowIfNull(processPaymentRequest);
        return _paymentProcessor.ProcessAsync(Settings, processPaymentRequest);
    }

    /// <inheritdoc />
    public Task PostProcessPaymentAsync(PostProcessPaymentRequest postProcessPaymentRequest)
    {
        ArgumentNullException.ThrowIfNull(postProcessPaymentRequest);
        // Nothing to do here: nop skips this hook for paid orders, so the order number is stamped on the gateway
        // transaction by NmiOrderPlacedEventConsumer instead.
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> HidePaymentMethodAsync(IList<ShoppingCartItem> cart)
    {
        ArgumentNullException.ThrowIfNull(cart);

        return Task.FromResult(string.IsNullOrWhiteSpace(Settings.PublicTokenizationKey));
    }

    /// <inheritdoc />
    public Task<decimal> GetAdditionalHandlingFeeAsync(IList<ShoppingCartItem> cart)
    {
        ArgumentNullException.ThrowIfNull(cart);
        return _orderTotalCalculationService.CalculatePaymentAdditionalFeeAsync(
            cart, Settings.AdditionalFee, Settings.AdditionalFeePercentage);
    }

    /// <inheritdoc />
    public Task<CapturePaymentResult> CaptureAsync(CapturePaymentRequest capturePaymentRequest)
    {
        ArgumentNullException.ThrowIfNull(capturePaymentRequest);
        return _orderOperationService.CaptureAsync(Settings, capturePaymentRequest);
    }

    /// <inheritdoc />
    public Task<RefundPaymentResult> RefundAsync(RefundPaymentRequest refundPaymentRequest)
    {
        ArgumentNullException.ThrowIfNull(refundPaymentRequest);
        return _orderOperationService.RefundAsync(Settings, refundPaymentRequest);
    }

    /// <inheritdoc />
    public Task<VoidPaymentResult> VoidAsync(VoidPaymentRequest voidPaymentRequest)
    {
        ArgumentNullException.ThrowIfNull(voidPaymentRequest);
        return _orderOperationService.VoidAsync(Settings, voidPaymentRequest);
    }

    /// <inheritdoc />
    public Task<ProcessPaymentResult> ProcessRecurringPaymentAsync(ProcessPaymentRequest processPaymentRequest)
    {
        ArgumentNullException.ThrowIfNull(processPaymentRequest);
        return Task.FromResult(new ProcessPaymentResult { Errors = new List<string> { "Recurring payments are not supported." } });
    }

    /// <inheritdoc />
    public Task<CancelRecurringPaymentResult> CancelRecurringPaymentAsync(CancelRecurringPaymentRequest cancelPaymentRequest)
    {
        ArgumentNullException.ThrowIfNull(cancelPaymentRequest);
        return Task.FromResult(new CancelRecurringPaymentResult { Errors = new List<string> { "Recurring payments are not supported." } });
    }

    /// <inheritdoc />
    public Task<bool> CanRePostProcessPaymentAsync(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public async Task<IList<string>> ValidatePaymentFormAsync(IFormCollection form)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (!TryGetPaymentToken(form, out _))
        {
            return new List<string>
            {
                await _localizationService.GetResourceAsync($"{NmiDefaults.LocaleResourcePrefix}.Tokenization.Required")
            };
        }

        return new List<string>();
    }

    /// <inheritdoc />
    public async Task<ProcessPaymentRequest> GetPaymentInfoAsync(IFormCollection form)
    {
        ArgumentNullException.ThrowIfNull(form);

        // Only the single-use Collect.js token is kept, and it is parked on the customer record rather than in
        // CustomValues: nopCommerce renders CustomValues on the confirm page and saves them with the order.
        var customer = await _workContext.GetCurrentCustomerAsync();
        var store = await _storeContext.GetCurrentStoreAsync();
        var token = TryGetPaymentToken(form, out var value) ? value : null;
        await _genericAttributeService.SaveAttributeAsync(customer, NmiDefaults.PaymentTokenAttribute, token, store.Id);

        // Masked metadata (brand, last four, expiry) lets the admin order page show which card was used.
        var request = new ProcessPaymentRequest();
        ApplyMaskedCardMetadata(request, form);
        return request;
    }

    /// <summary>
    /// Copies the masked card details Collect.js reported into nop's standard card fields. The "number" is always
    /// twelve asterisks plus the last four digits, so nothing sensitive is ever stored.
    /// </summary>
    internal static void ApplyMaskedCardMetadata(ProcessPaymentRequest request, IFormCollection form)
    {
        var last4 = Single(form, NmiDefaults.CardLast4FormKey);
        if (last4?.Length == 4 && last4.All(char.IsAsciiDigit))
            request.CreditCardNumber = new string('*', 12) + last4;

        var brand = Single(form, NmiDefaults.CardBrandFormKey);
        if (!string.IsNullOrEmpty(brand) && brand.Length <= 20 && brand.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '_' or '-'))
            request.CreditCardType = char.ToUpperInvariant(brand[0]) + brand[1..].ToLowerInvariant();

        var expiration = Single(form, NmiDefaults.CardExpirationFormKey);
        if (expiration?.Length == 4 && expiration.All(char.IsAsciiDigit) &&
            int.TryParse(expiration[..2], out var month) && month is >= 1 and <= 12 &&
            int.TryParse(expiration[2..], out var year))
        {
            request.CreditCardExpireMonth = month;
            request.CreditCardExpireYear = 2000 + year;
        }
    }

    private static string Single(IFormCollection form, string key)
    {
        var values = form[key];
        return values.Count == 1 ? values[0]?.Trim() : null;
    }

    /// <inheritdoc />
    public Type GetPublicViewComponent()
    {
        return typeof(NmiPaymentInfoViewComponent);
    }

    public override string GetConfigurationPageUrl() =>
        $"{_webHelper.GetStoreLocation()}Admin/PaymentNmi/Configure";

    /// <inheritdoc />
    public async Task<string> GetPaymentMethodDescriptionAsync()
    {
        return await _localizationService.GetResourceAsync(
            $"{NmiDefaults.LocaleResourcePrefix}.PaymentMethodDescription");
    }

    /// <inheritdoc />
    public override async Task InstallAsync()
    {
        if (!_paymentSettings.ActivePaymentMethodSystemNames.Contains(NmiDefaults.SystemName))
        {
            _paymentSettings.ActivePaymentMethodSystemNames.Add(NmiDefaults.SystemName);
            await _settingService.SaveSettingAsync(_paymentSettings);
        }

        await _localizationService.AddOrUpdateLocaleResourceAsync(new Dictionary<string, string>
        {
            [$"{NmiDefaults.LocaleResourcePrefix}.PaymentMethodDescription"] = "Pay securely by card",
            [$"{NmiDefaults.LocaleResourcePrefix}.FoundationUnavailable"] =
                "NMI checkout will be available after the gateway is configured.",
            [$"{NmiDefaults.LocaleResourcePrefix}.Fields.UseSandbox"] = "Use NMI sandbox",
            [$"{NmiDefaults.LocaleResourcePrefix}.Fields.UseSandbox.Hint"] =
                "Send all requests to sandbox.nmi.com with sandbox keys. Turn off to use the production gateway host below.",
            [$"{NmiDefaults.LocaleResourcePrefix}.Fields.GatewayHost"] = "Gateway host",
            [$"{NmiDefaults.LocaleResourcePrefix}.Fields.GatewayHost.Hint"] =
                "Production gateway host name that serves transact.php and Collect.js, for example tbtech.transactiongateway.com. No scheme or path.",
            [$"{NmiDefaults.LocaleResourcePrefix}.Fields.PublicTokenizationKey"] = "Public tokenization key",
            [$"{NmiDefaults.LocaleResourcePrefix}.Fields.PublicTokenizationKey.Hint"] =
                "Public security key with the Tokenization permission. Used by Collect.js in the shopper's browser.",
            [$"{NmiDefaults.LocaleResourcePrefix}.Fields.PrivateApiKey"] = "Private API security key",
            [$"{NmiDefaults.LocaleResourcePrefix}.Fields.PrivateApiKey.Hint"] =
                "Private security key with the API permission. Sent as security_key to the Payment API; never shown again after saving.",
            [$"{NmiDefaults.LocaleResourcePrefix}.Fields.WebhookSigningKey"] = "Webhook signing key",
            [$"{NmiDefaults.LocaleResourcePrefix}.Fields.WebhookSigningKey.Hint"] =
                "Signing key from the merchant portal webhook configuration. Used to verify the Webhook-Signature header.",
            [$"{NmiDefaults.LocaleResourcePrefix}.Fields.TransactionMode"] = "Transaction mode",
            [$"{NmiDefaults.LocaleResourcePrefix}.Fields.ProcessorId"] = "Processor ID",
            [$"{NmiDefaults.LocaleResourcePrefix}.Fields.ProcessorId.Hint"] =
                "Optional. Routes transactions to a specific processor when the account uses multiple MIDs.",
            [$"{NmiDefaults.LocaleResourcePrefix}.Fields.DuplicateCheckSeconds"] = "Duplicate check seconds",
            [$"{NmiDefaults.LocaleResourcePrefix}.Fields.DuplicateCheckSeconds.Hint"] =
                "Optional override of the processor's duplicate-transaction window, in seconds. Leave at 0 to keep the processor's own setting; some processors reject any override.",
            [$"{NmiDefaults.LocaleResourcePrefix}.Fields.AdditionalFee"] = "Additional fee",
            [$"{NmiDefaults.LocaleResourcePrefix}.Fields.AdditionalFeePercentage"] = "Use percentage fee",
            [$"{NmiDefaults.LocaleResourcePrefix}.Fields.EnableDebugLogging"] = "Enable sanitized debug logging",
            [$"{NmiDefaults.LocaleResourcePrefix}.Tokenization.CardNumber"] = "Card number",
            [$"{NmiDefaults.LocaleResourcePrefix}.Tokenization.Expiration"] = "Expiration",
            [$"{NmiDefaults.LocaleResourcePrefix}.Tokenization.SecurityCode"] = "Security code",
            [$"{NmiDefaults.LocaleResourcePrefix}.Tokenization.Loading"] = "Secure payment fields are loading.",
            [$"{NmiDefaults.LocaleResourcePrefix}.Tokenization.Incomplete"] = "Enter your card details, then continue.",
            [$"{NmiDefaults.LocaleResourcePrefix}.Tokenization.Processing"] = "Securing your card details...",
            [$"{NmiDefaults.LocaleResourcePrefix}.Tokenization.Ready"] = "Payment details are ready.",
            [$"{NmiDefaults.LocaleResourcePrefix}.Tokenization.Failure"] =
                "Secure payment fields could not be loaded. Refresh the page and try again.",
            [$"{NmiDefaults.LocaleResourcePrefix}.Tokenization.Required"] =
                "Enter valid payment details before continuing.",
            [$"{NmiDefaults.LocaleResourcePrefix}.Processing.MissingToken"] =
                "Your secure payment token is missing or expired. Return to payment details and try again.",
            [$"{NmiDefaults.LocaleResourcePrefix}.Processing.InProgress"] =
                "This payment is already being submitted. Wait for it to finish before trying again.",
            [$"{NmiDefaults.LocaleResourcePrefix}.Processing.NotConfigured"] =
                "This payment method is not configured. Choose another payment method.",
            [$"{NmiDefaults.LocaleResourcePrefix}.Processing.Declined"] =
                "The payment was declined. Check your payment details or use another payment method.",
            [$"{NmiDefaults.LocaleResourcePrefix}.Processing.Failed"] =
                "The payment could not be processed. Check your details or use another payment method.",
            [$"{NmiDefaults.LocaleResourcePrefix}.Processing.Unauthorized"] =
                "The payment service is temporarily unavailable. Choose another payment method.",
            [$"{NmiDefaults.LocaleResourcePrefix}.Processing.Ambiguous"] =
                "The payment result could not be confirmed. Do not retry yet; contact the store for assistance.",
            [$"{NmiDefaults.LocaleResourcePrefix}.Operations.InvalidState"] =
                "The order is not eligible for this payment operation.",
            [$"{NmiDefaults.LocaleResourcePrefix}.Operations.InvalidAmount"] =
                "The requested amount is invalid or exceeds the remaining eligible amount.",
            [$"{NmiDefaults.LocaleResourcePrefix}.Operations.MissingTransaction"] =
                "The original NMI transaction identifier is missing or invalid.",
            [$"{NmiDefaults.LocaleResourcePrefix}.Operations.Declined"] =
                "NMI declined the requested payment operation.",
            [$"{NmiDefaults.LocaleResourcePrefix}.Operations.Failed"] =
                "NMI could not complete the requested payment operation.",
            [$"{NmiDefaults.LocaleResourcePrefix}.Operations.Unauthorized"] =
                "NMI rejected the configured API credentials.",
            [$"{NmiDefaults.LocaleResourcePrefix}.Operations.Ambiguous"] =
                "The NMI result could not be confirmed. Do not retry this operation until the transaction is reconciled."
        });

        await _settingService.SaveSettingAsync(new NmiSettings());

        if (await _scheduleTaskService.GetTaskByTypeAsync(NmiDefaults.WebhookCleanupTaskType) is null)
        {
            await _scheduleTaskService.InsertTaskAsync(new ScheduleTask
            {
                Enabled = true,
                LastEnabledUtc = DateTime.UtcNow,
                Seconds = 24 * 60 * 60,
                Name = "NMI webhook record cleanup",
                Type = NmiDefaults.WebhookCleanupTaskType
            });
        }

        await base.InstallAsync();
    }

    /// <inheritdoc />
    public override async Task UninstallAsync()
    {
        if (_paymentSettings.ActivePaymentMethodSystemNames.Remove(NmiDefaults.SystemName))
            await _settingService.SaveSettingAsync(_paymentSettings);

        await _settingService.DeleteSettingAsync<NmiSettings>();
        if (await _scheduleTaskService.GetTaskByTypeAsync(NmiDefaults.WebhookCleanupTaskType) is { } task)
            await _scheduleTaskService.DeleteTaskAsync(task);
        await _localizationService.DeleteLocaleResourcesAsync(NmiDefaults.LocaleResourcePrefix);
        await base.UninstallAsync();
    }

    /// <inheritdoc />
    public bool SupportCapture => true;

    /// <inheritdoc />
    public bool SupportPartiallyRefund => true;

    /// <inheritdoc />
    public bool SupportRefund => true;

    /// <inheritdoc />
    public bool SupportVoid => true;

    /// <inheritdoc />
    public RecurringPaymentType RecurringPaymentType => RecurringPaymentType.NotSupported;

    /// <inheritdoc />
    public PaymentMethodType PaymentMethodType => PaymentMethodType.Standard;

    /// <inheritdoc />
    public bool SkipPaymentInfo => false;

    internal static bool TryGetPaymentToken(IFormCollection form, out string token)
    {
        var values = form[NmiDefaults.PaymentTokenFormKey];
        token = values.Count == 1 ? values[0]?.Trim() : null;
        return IsValidPaymentToken(token);
    }

    internal static bool IsValidPaymentToken(string token) =>
        !string.IsNullOrWhiteSpace(token) && token.Length <= 512 && !token.Any(char.IsWhiteSpace);
}
