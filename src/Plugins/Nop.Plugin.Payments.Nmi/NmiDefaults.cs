namespace Nop.Plugin.Payments.Nmi;

/// <summary>
/// Contains plugin constants.
/// </summary>
public static class NmiDefaults
{
    /// <summary>
    /// Gets the plugin system name.
    /// </summary>
    public const string SystemName = "Payments.Nmi";

    /// <summary>
    /// Gets the locale resource prefix.
    /// </summary>
    public const string LocaleResourcePrefix = "Plugins.Payments.Nmi";

    public const string MaskedSecret = "••••••••";
    public const string PaymentTokenFormKey = "NmiPaymentToken";

    /// <summary>
    /// Masked card metadata that Collect.js returns alongside the token (last four digits, brand, expiry MMYY).
    /// Never a full card number; used only to populate nop's masked card fields on the order.
    /// </summary>
    public const string CardLast4FormKey = "NmiCardLast4";
    public const string CardBrandFormKey = "NmiCardBrand";
    public const string CardExpirationFormKey = "NmiCardExpiration";

    /// <summary>
    /// Store-scoped customer generic attribute that carries the single-use Collect.js token from the payment-info
    /// step to order placement. It is deliberately not a <c>ProcessPaymentRequest.CustomValues</c> entry, because
    /// nopCommerce prints those on the confirm-order page and persists them with the order.
    /// </summary>
    public const string PaymentTokenAttribute = "Nmi.PaymentToken";

    /// <summary>
    /// Session key nopCommerce 4.60 uses to hold the checkout <c>ProcessPaymentRequest</c> between the
    /// payment-info step and order placement. Cleared once the single-use token has been sent to NMI.
    /// </summary>
    public const string CheckoutPaymentInfoSessionKey = "OrderPaymentInfo";

    public const string WebhookRouteName = "Plugins.Payments.Nmi.Webhook";
    public const string WebhookRoute = "nmi/webhooks/transactions";
    public const string WebhookSignatureHeader = "Webhook-Signature";
    public const string WebhookCleanupTaskType = "Nop.Plugin.Payments.Nmi.Services.NmiWebhookCleanupTask, Nop.Plugin.Payments.Nmi";
    public const int WebhookRetentionDays = 90;

    /// <summary>
    /// NMI's public sandbox gateway host.
    /// </summary>
    public const string SandboxHost = "sandbox.nmi.com";

    /// <summary>
    /// Default production gateway host (the reseller-branded gateway that serves transact.php and Collect.js).
    /// </summary>
    public const string DefaultGatewayHost = "tbtech.transactiongateway.com";

    /// <summary>
    /// Classic Payment API endpoint, relative to the gateway origin.
    /// </summary>
    public const string TransactApiPath = "api/transact.php";

    /// <summary>
    /// Classic Query API endpoint, relative to the gateway origin.
    /// </summary>
    public const string QueryApiPath = "api/query.php";

    /// <summary>
    /// Collect.js browser tokenizer, relative to the gateway origin. It must be loaded from the gateway, never self-hosted.
    /// </summary>
    public const string CollectJsPath = "token/Collect.js";

    public static readonly Uri SandboxBaseUri = new($"https://{SandboxHost}/");
    public static readonly Uri DefaultProductionBaseUri = new($"https://{DefaultGatewayHost}/");
}
