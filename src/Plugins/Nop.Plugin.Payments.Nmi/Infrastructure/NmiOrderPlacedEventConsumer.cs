using Nop.Core.Domain.Orders;
using Nop.Plugin.Payments.Nmi.Services;
using Nop.Plugin.Payments.Nmi.Services.Api;
using Nop.Services.Common;
using Nop.Services.Configuration;
using Nop.Services.Events;
using Nop.Services.Orders;

namespace Nop.Plugin.Payments.Nmi.Infrastructure;

/// <summary>
/// Finishes what could not be done while the sale was sent, because nopCommerce had not created the order yet:
/// tags the gateway transaction with the store order number and stores the Kount score on the order. nop skips
/// <c>PostProcessPayment</c> for orders that are already paid, so the order-placed event is the dependable hook for
/// both sale and authorize modes.
/// </summary>
public class NmiOrderPlacedEventConsumer : IConsumer<OrderPlacedEvent>
{
    private readonly IGenericAttributeService _genericAttributeService;
    private readonly IOrderService _orderService;
    private readonly ISettingService _settingService;
    private readonly NmiLogger _log;
    private readonly NmiOrderOperationService _orderOperationService;

    public NmiOrderPlacedEventConsumer(IGenericAttributeService genericAttributeService,
        IOrderService orderService,
        ISettingService settingService,
        NmiLogger log,
        NmiOrderOperationService orderOperationService)
    {
        _genericAttributeService = genericAttributeService;
        _orderService = orderService;
        _settingService = settingService;
        _log = log ?? NmiLogger.None;
        _orderOperationService = orderOperationService;
    }

    public async Task HandleEventAsync(OrderPlacedEvent eventMessage)
    {
        var order = eventMessage?.Order;
        if (order is null || order.PaymentMethodSystemName != NmiDefaults.SystemName)
            return;

        var settings = await _settingService.LoadSettingAsync<NmiSettings>(order.StoreId);
        await RecordKountScoreAsync(settings, order);
        await _orderOperationService.StampOrderReferenceAsync(settings, order);
    }

    /// <summary>
    /// Saves the Kount score and its risk band as generic attributes of the order so they can be queried on their
    /// own, and adds a private order note, because the nopCommerce order page shows neither generic attributes nor
    /// the transaction result. Never throws: a failure here must not disturb an order that has already been paid.
    /// </summary>
    private async Task RecordKountScoreAsync(NmiSettings settings, Order order)
    {
        if (!TryGetKountScore(order, out var score))
            return;

        try
        {
            var value = NmiResponseCodes.FormatKountScore(score);
            var band = NmiResponseCodes.DescribeKountBand(score);
            await _genericAttributeService.SaveAttributeAsync(order, NmiDefaults.KountScoreAttribute, value);
            await _genericAttributeService.SaveAttributeAsync(order, NmiDefaults.KountBandAttribute, band);
            await _orderService.InsertOrderNoteAsync(new OrderNote
            {
                OrderId = order.Id,
                Note = BuildKountNote(score),
                DisplayToCustomer = false,
                CreatedOnUtc = DateTime.UtcNow
            });
            await _log.DebugAsync(settings, $"kount score stored on order; order={order.Id}; kount_score={value} ({band}).");
        }
        catch (Exception exception)
        {
            await _log.WarningAsync(
                $"kount score could not be stored on order {order.Id}; error_type={exception.GetType().Name}.", exception);
        }
    }

    /// <summary>
    /// Text of the private order note, for example <c>Kount score: 89.0 (safe)</c>.
    /// </summary>
    internal static string BuildKountNote(decimal score) =>
        $"Kount score: {NmiResponseCodes.FormatKountScore(score)} ({NmiResponseCodes.DescribeKountBand(score)})";

    /// <summary>
    /// Reads the score from the gateway result nopCommerce copied onto the order: the capture result for a sale,
    /// the authorization result for authorize-only.
    /// </summary>
    internal static bool TryGetKountScore(Order order, out decimal score)
    {
        score = 0;
        return order is not null &&
               (NmiResponseCodes.TryReadKountScore(order.CaptureTransactionResult, out score) ||
                NmiResponseCodes.TryReadKountScore(order.AuthorizationTransactionResult, out score));
    }
}
