using Nop.Web.Framework.Models;

namespace Nop.Plugin.Payments.Nmi.Models;

public record PaymentInfoModel : BaseNopModel
{
    public string PublicTokenizationKey { get; init; }

    /// <summary>
    /// Absolute URL of Collect.js on the configured gateway. Null when the gateway configuration is unusable.
    /// </summary>
    public string CollectJsUrl { get; init; }

    public string CardNumberLabel { get; init; }
    public string ExpirationLabel { get; init; }
    public string SecurityCodeLabel { get; init; }
    public string LoadingText { get; init; }
    public string IncompleteText { get; init; }
    public string ProcessingText { get; init; }
    public string ReadyText { get; init; }
    public string FailureText { get; init; }
}
