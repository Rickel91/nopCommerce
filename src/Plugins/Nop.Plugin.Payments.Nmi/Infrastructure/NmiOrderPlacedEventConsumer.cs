using Nop.Core.Domain.Orders;
using Nop.Plugin.Payments.Nmi.Services;
using Nop.Services.Configuration;
using Nop.Services.Events;

namespace Nop.Plugin.Payments.Nmi.Infrastructure;

/// <summary>
/// Tags the gateway transaction with the store order number as soon as nopCommerce assigns it. The order number does
/// not exist when the sale is sent, and nop skips <c>PostProcessPayment</c> for orders that are already paid, so the
/// order-placed event is the dependable hook for both sale and authorize modes.
/// </summary>
public class NmiOrderPlacedEventConsumer : IConsumer<OrderPlacedEvent>
{
    private readonly ISettingService _settingService;
    private readonly NmiOrderOperationService _orderOperationService;

    public NmiOrderPlacedEventConsumer(ISettingService settingService, NmiOrderOperationService orderOperationService)
    {
        _settingService = settingService;
        _orderOperationService = orderOperationService;
    }

    public async Task HandleEventAsync(OrderPlacedEvent eventMessage)
    {
        var order = eventMessage?.Order;
        if (order is null || order.PaymentMethodSystemName != NmiDefaults.SystemName)
            return;

        var settings = await _settingService.LoadSettingAsync<NmiSettings>(order.StoreId);
        await _orderOperationService.StampOrderReferenceAsync(settings, order);
    }
}
