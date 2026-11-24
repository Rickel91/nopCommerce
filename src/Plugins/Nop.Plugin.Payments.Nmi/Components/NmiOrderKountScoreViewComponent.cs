using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Nop.Core;
using Nop.Core.Domain.Orders;
using Nop.Plugin.Payments.Nmi.Infrastructure;
using Nop.Plugin.Payments.Nmi.Services.Api;
using Nop.Services.Common;
using Nop.Services.Localization;
using Nop.Services.Orders;
using Nop.Web.Framework.Components;
using Nop.Web.Framework.Models;

namespace Nop.Plugin.Payments.Nmi.Components;

/// <summary>
/// Shows the Kount score of an NMI order on the admin order page, as a row placed directly below the Store row of
/// the order information. nopCommerce offers no widget zone at that position, so the row is rendered in the order
/// details widget zone and moved into place in the browser; when the Store row cannot be found the row stays where
/// it was rendered.
/// </summary>
public class NmiOrderKountScoreViewComponent : NopViewComponent
{
    internal const string RowId = "nmi-kount-score-row";

    private readonly IGenericAttributeService _genericAttributeService;
    private readonly ILocalizationService _localizationService;
    private readonly IOrderService _orderService;
    private readonly IWorkContext _workContext;

    public NmiOrderKountScoreViewComponent(IGenericAttributeService genericAttributeService,
        ILocalizationService localizationService,
        IOrderService orderService,
        IWorkContext workContext)
    {
        _genericAttributeService = genericAttributeService;
        _localizationService = localizationService;
        _orderService = orderService;
        _workContext = workContext;
    }

    public async Task<IViewComponentResult> InvokeAsync(string widgetZone, object additionalData)
    {
        if (additionalData is not BaseNopEntityModel model || model.Id <= 0)
            return Content(string.Empty);

        // Risk data is for store staff, not for vendors.
        if (await _workContext.GetCurrentVendorAsync() is not null)
            return Content(string.Empty);

        var order = await _orderService.GetOrderByIdAsync(model.Id);
        if (order is null || order.PaymentMethodSystemName != NmiDefaults.SystemName)
            return Content(string.Empty);

        var languageId = (await _workContext.GetWorkingLanguageAsync()).Id;
        var label = await ResourceAsync("Order.KountScore", languageId, "Kount score");
        var hint = await ResourceAsync("Order.KountScore.Hint", languageId,
            "Kount Omniscore returned by the NMI gateway for this payment: 0-29 high risk, 30-69 review, 70-99 safe.");
        var missing = await ResourceAsync("Order.KountScore.NotReturned", languageId, "Not returned by the gateway");

        var value = await GetDisplayValueAsync(order) ?? missing;
        return new HtmlContentViewComponentResult(new HtmlString(BuildHtml(label, hint, value)));
    }

    /// <summary>
    /// The stored order attributes when present; otherwise the score recovered from the transaction result, which
    /// covers orders placed before the attributes were introduced. Null when the gateway returned no score.
    /// </summary>
    private async Task<string> GetDisplayValueAsync(Order order)
    {
        var score = await _genericAttributeService.GetAttributeAsync<string>(order, NmiDefaults.KountScoreAttribute);
        if (!string.IsNullOrWhiteSpace(score))
        {
            var band = await _genericAttributeService.GetAttributeAsync<string>(order, NmiDefaults.KountBandAttribute);
            return string.IsNullOrWhiteSpace(band) ? score.Trim() : $"{score.Trim()} ({band.Trim()})";
        }

        return NmiOrderPlacedEventConsumer.TryGetKountScore(order, out var recovered)
            ? FormatScore(recovered)
            : null;
    }

    internal static string FormatScore(decimal score) =>
        $"{NmiResponseCodes.FormatKountScore(score)} ({NmiResponseCodes.DescribeKountBand(score)})";

    /// <summary>
    /// Builds the row in the admin theme's own markup, hidden until the script has placed it. The script copies the
    /// column classes of the Store row so the new row lines up in themes that use a different grid split.
    /// </summary>
    internal static string BuildHtml(string label, string hint, string value)
    {
        var html = HtmlEncoder.Default;
        return $$"""
            <div class="form-group row" id="{{RowId}}" style="display:none">
                <div class="col-md-3">
                    <div class="label-wrapper">
                        <label class="col-form-label">{{html.Encode(label)}}</label>
                        <div title="{{html.Encode(hint)}}" data-toggle="tooltip" class="ico-help"><i class="fas fa-question-circle"></i></div>
                    </div>
                </div>
                <div class="col-md-9">
                    <div class="form-text-row">{{html.Encode(value)}}</div>
                </div>
            </div>
            <script>
                (function () {
                    function place() {
                        var row = document.getElementById('{{RowId}}');
                        if (!row) return;
                        var storeLabel = document.querySelector('label[for="StoreName"]');
                        var storeRow = storeLabel ? storeLabel.closest('.form-group') : null;
                        if (storeRow && storeRow.parentNode) {
                            if (storeRow.children.length === 2 && row.children.length === 2) {
                                row.children[0].className = storeRow.children[0].className;
                                row.children[1].className = storeRow.children[1].className;
                            }
                            storeRow.parentNode.insertBefore(row, storeRow.nextSibling);
                        }
                        row.style.display = '';
                    }
                    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', place);
                    else place();
                })();
            </script>
            """;
    }

    private Task<string> ResourceAsync(string name, int languageId, string defaultValue) =>
        _localizationService.GetResourceAsync($"{NmiDefaults.LocaleResourcePrefix}.{name}", languageId, false, defaultValue);
}
