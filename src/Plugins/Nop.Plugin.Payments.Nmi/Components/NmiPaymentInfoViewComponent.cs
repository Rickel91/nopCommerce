using Microsoft.AspNetCore.Mvc;
using Nop.Plugin.Payments.Nmi.Models;
using Nop.Plugin.Payments.Nmi.Services;
using Nop.Services.Localization;
using Nop.Web.Framework.Components;

namespace Nop.Plugin.Payments.Nmi.Components;

/// <summary>
/// Renders the Collect.js hosted card fields for the public checkout.
/// </summary>
public class NmiPaymentInfoViewComponent : NopViewComponent
{
    private readonly ILocalizationService _localizationService;
    private readonly NmiSettings _settings;

    public NmiPaymentInfoViewComponent(ILocalizationService localizationService, NmiSettings settings)
    {
        _localizationService = localizationService;
        _settings = settings;
    }

    /// <summary>
    /// Invokes the component.
    /// </summary>
    public async Task<IViewComponentResult> InvokeAsync()
    {
        string collectJsUrl = null;
        try
        {
            collectJsUrl = NmiHttpClient.GetCollectJsUri(_settings).AbsoluteUri;
        }
        catch (ArgumentException)
        {
            // An invalid gateway host leaves the fields in their failure state instead of loading a script from an unknown origin.
        }

        var model = new PaymentInfoModel
        {
            PublicTokenizationKey = collectJsUrl is null ? null : _settings.PublicTokenizationKey,
            CollectJsUrl = collectJsUrl,
            CardNumberLabel = await GetResourceAsync("Tokenization.CardNumber"),
            ExpirationLabel = await GetResourceAsync("Tokenization.Expiration"),
            SecurityCodeLabel = await GetResourceAsync("Tokenization.SecurityCode"),
            LoadingText = await GetResourceAsync("Tokenization.Loading"),
            IncompleteText = await GetResourceAsync("Tokenization.Incomplete"),
            ProcessingText = await GetResourceAsync("Tokenization.Processing"),
            ReadyText = await GetResourceAsync("Tokenization.Ready"),
            FailureText = await GetResourceAsync("Tokenization.Failure")
        };

        return View("~/Plugins/Payments.Nmi/Views/PaymentInfo.cshtml", model);
    }

    private Task<string> GetResourceAsync(string name) =>
        _localizationService.GetResourceAsync($"{NmiDefaults.LocaleResourcePrefix}.{name}");
}
