using System.Globalization;
using LinqToDB;
using Nop.Core.Domain.Orders;
using Nop.Core.Domain.Payments;
using Nop.Data;
using Nop.Plugin.Payments.Nmi.Domain;
using Nop.Plugin.Payments.Nmi.Services.Api;
using Nop.Services.Configuration;
using Nop.Services.Logging;
using Nop.Services.Orders;

namespace Nop.Plugin.Payments.Nmi.Services;

public class NmiWebhookProcessor
{
    private readonly NmiLogger _log;
    private readonly IRepository<Order> _orderRepository;
    private readonly IOrderProcessingService _orderProcessingService;
    private readonly IOrderService _orderService;
    private readonly ISettingService _settingService;
    private readonly NmiWebhookRecordService _recordService;

    public NmiWebhookProcessor(NmiLogger log,
        IRepository<Order> orderRepository,
        IOrderProcessingService orderProcessingService,
        IOrderService orderService,
        ISettingService settingService,
        NmiWebhookRecordService recordService)
    {
        _log = log ?? NmiLogger.None;
        _orderRepository = orderRepository;
        _orderProcessingService = orderProcessingService;
        _orderService = orderService;
        _settingService = settingService;
        _recordService = recordService;
    }

    public async Task<NmiWebhookProcessingResult> ProcessAsync(NmiWebhookEvent webhookEvent,
        IReadOnlySet<int> authorizedStoreIds = null)
    {
        if (!NmiWebhookEventDescriptor.TryCreate(webhookEvent, out var descriptor))
            return NmiWebhookProcessingResult.Malformed;

        Order order;
        try
        {
            order = await FindOrderAsync(webhookEvent.EventBody.OrderId, descriptor.TransactionId);
        }
        catch (Exception exception)
        {
            await TryWarningAsync(
                $"NMI webhook event={Safe(webhookEvent.EventId)} type={descriptor.EventType} " +
                $"transaction={Safe(descriptor.TransactionId)} correlation lookup failed; will be retried by NMI; " +
                $"error_type={exception.GetType().Name}.", exception);
            return NmiWebhookProcessingResult.Retry;
        }

        if (order is not null && authorizedStoreIds is not null && !authorizedStoreIds.Contains(order.StoreId))
            return NmiWebhookProcessingResult.Unauthorized;

        var acquired = await _recordService.AcquireAsync(new NmiWebhookRecord
        {
            EventId = webhookEvent.EventId,
            EventType = webhookEvent.EventType,
            TransactionId = descriptor.TransactionId,
            ProcessingState = NmiWebhookProcessingState.Processing,
            ReceivedOnUtc = DateTime.UtcNow,
            OccurredOnUtc = descriptor.OccurredOnUtc
        });
        if (acquired.Status == NmiWebhookAcquireStatus.Completed)
            return NmiWebhookProcessingResult.Accepted;
        if (acquired.Status == NmiWebhookAcquireStatus.InProgress)
            return NmiWebhookProcessingResult.InProgress;

        var record = acquired.Record;
        try
        {
            if (order is null)
            {
                await _recordService.CompleteAsync(record, NmiWebhookProcessingState.Failed, "order_not_found");
                await TryWarningAsync(
                    $"NMI webhook event={Safe(webhookEvent.EventId)} transaction={Safe(descriptor.TransactionId)} " +
                    "could not be correlated to an order.");
                return NmiWebhookProcessingResult.Retry;
            }

            record.OrderId = order.Id;
            var settings = await _settingService.LoadSettingAsync<NmiSettings>(order.StoreId);
            if (webhookEvent.EventBody.Features is null ||
                settings.UseSandbox != webhookEvent.EventBody.Features.IsTestMode)
            {
                await AddNoteAsync(order, descriptor, webhookEvent.EventId, "environment_mismatch");
                await _recordService.CompleteAsync(record, NmiWebhookProcessingState.Ignored,
                    "environment_mismatch", order.Id);
                await _log.WarningAsync(
                    $"NMI webhook event={Safe(webhookEvent.EventId)} type={descriptor.EventType} order={order.Id} ignored; " +
                    $"reason=environment_mismatch; store_use_sandbox={settings.UseSandbox}; " +
                    $"event_is_test_mode={webhookEvent.EventBody.Features?.IsTestMode.ToString() ?? "unknown"}.");
                return NmiWebhookProcessingResult.Accepted;
            }

            var latestOccurrence = await _recordService.GetLatestProcessedOccurrenceAsync(order.Id, record.Id);
            if (NmiWebhookStatePolicy.IsStale(descriptor.OccurredOnUtc, latestOccurrence))
            {
                await AddNoteAsync(order, descriptor, webhookEvent.EventId, "stale_event_ignored");
                await _recordService.CompleteAsync(record, NmiWebhookProcessingState.Ignored,
                    "stale_event_ignored", order.Id);
                return NmiWebhookProcessingResult.Accepted;
            }

            if (descriptor.Outcome != NmiWebhookOutcome.Success)
            {
                var diagnostic = descriptor.Outcome == NmiWebhookOutcome.Unknown
                    ? "unknown_outcome_requires_reconciliation"
                    : "gateway_reported_failure";
                await AddNoteAsync(order, descriptor, webhookEvent.EventId, diagnostic);
                await _recordService.CompleteAsync(record, NmiWebhookProcessingState.Processed,
                    diagnostic, order.Id);
                return NmiWebhookProcessingResult.Accepted;
            }

            var transition = await ApplySuccessAsync(order, descriptor);
            await AddNoteAsync(order, descriptor, webhookEvent.EventId, transition.Diagnostic);
            await _recordService.CompleteAsync(record,
                transition.Changed ? NmiWebhookProcessingState.Processed : NmiWebhookProcessingState.Ignored,
                transition.Diagnostic, order.Id);
            await _log.DebugAsync(settings,
                $"NMI webhook event={Safe(webhookEvent.EventId)} type={descriptor.EventType} order={order.Id} " +
                $"transaction={Safe(descriptor.TransactionId)} amount={descriptor.Amount:0.00} " +
                $"result={transition.Diagnostic}; order_changed={transition.Changed}; payment_status={order.PaymentStatus}.");
            return NmiWebhookProcessingResult.Accepted;
        }
        catch (Exception exception)
        {
            await _recordService.CompleteAsync(record, NmiWebhookProcessingState.Failed,
                $"processing_error:{exception.GetType().Name}", record.OrderId);
            await _log.ErrorAsync(
                $"NMI webhook event={Safe(webhookEvent.EventId)} type={descriptor.EventType} " +
                $"transaction={Safe(descriptor.TransactionId)} order={order?.Id ?? 0} failed while applying; " +
                $"will be retried by NMI; error_type={exception.GetType().Name}.", exception);
            return NmiWebhookProcessingResult.Retry;
        }
    }

    private async Task<NmiWebhookTransition> ApplySuccessAsync(Order order, NmiWebhookEventDescriptor descriptor)
    {
        if (NmiWebhookStatePolicy.IsTerminal(order.PaymentStatus))
            return new(false, "terminal_state_preserved");

        var changed = false;
        var diagnostic = "already_reconciled";
        switch (descriptor.Operation)
        {
            case "sale" when order.PaymentStatus == PaymentStatus.Pending:
                order.CaptureTransactionId = descriptor.TransactionId;
                order.PaymentStatus = PaymentStatus.Paid;
                order.PaidDateUtc ??= DateTime.UtcNow;
                changed = true;
                diagnostic = "sale_reconciled_paid";
                break;
            case "auth" when order.PaymentStatus == PaymentStatus.Pending:
                order.AuthorizationTransactionId = descriptor.TransactionId;
                order.PaymentStatus = PaymentStatus.Authorized;
                changed = true;
                diagnostic = "authorization_reconciled";
                break;
            case "capture" when order.PaymentStatus == PaymentStatus.Authorized:
                order.CaptureTransactionId = descriptor.TransactionId;
                order.PaymentStatus = PaymentStatus.Paid;
                order.PaidDateUtc ??= DateTime.UtcNow;
                changed = true;
                diagnostic = "capture_reconciled_paid";
                break;
            case "void" when order.PaymentStatus is PaymentStatus.Authorized or PaymentStatus.Paid:
                order.PaymentStatus = PaymentStatus.Voided;
                changed = true;
                diagnostic = "void_reconciled";
                break;
            case "refund" when order.PaymentStatus is PaymentStatus.Paid or PaymentStatus.PartiallyRefunded:
                (changed, diagnostic) = await ApplyRefundAsync(order, descriptor);
                break;
            default:
                diagnostic = "state_transition_not_applicable";
                break;
        }

        if (!changed)
            return new(false, diagnostic);

        await _orderService.UpdateOrderAsync(order);
        await _orderProcessingService.CheckOrderStatusAsync(order);
        return new(true, diagnostic);
    }

    private async Task<(bool Changed, string Diagnostic)> ApplyRefundAsync(Order order,
        NmiWebhookEventDescriptor descriptor)
    {
        if (descriptor.Amount <= 0)
            return (false, "invalid_refund_amount");

        var notes = await _orderService.GetOrderNotesByOrderIdAsync(order.Id, false);
        var marker = $"NMI refund; amount={descriptor.Amount.ToString("0.00", CultureInfo.InvariantCulture)}; " +
                     $"root_transaction={descriptor.TransactionId};";
        var synchronousCompletionExists = notes.Any(note =>
            note.Note?.StartsWith(marker, StringComparison.Ordinal) == true &&
            Math.Abs((note.CreatedOnUtc - descriptor.OccurredOnUtc).TotalMinutes) <= 10);
        if (synchronousCompletionExists)
            return (false, "synchronous_refund_already_recorded");

        var refundedAmount = order.RefundedAmount + descriptor.Amount;
        if (refundedAmount > order.OrderTotal)
            return (false, "refund_exceeds_order_total");

        order.RefundedAmount = refundedAmount;
        order.PaymentStatus = refundedAmount == order.OrderTotal
            ? PaymentStatus.Refunded
            : PaymentStatus.PartiallyRefunded;
        return (true, order.PaymentStatus == PaymentStatus.Refunded
            ? "full_refund_reconciled"
            : "partial_refund_reconciled");
    }

    protected virtual async Task<Order> FindOrderAsync(string orderCorrelation, string transactionId)
    {
        if (Guid.TryParse(orderCorrelation, out var orderGuid))
        {
            var order = await _orderService.GetOrderByGuidAsync(orderGuid);
            if (order?.PaymentMethodSystemName == NmiDefaults.SystemName)
                return order;
        }

        return await _orderRepository.Table.FirstOrDefaultAsync(order =>
            order.PaymentMethodSystemName == NmiDefaults.SystemName &&
            (order.AuthorizationTransactionId == transactionId || order.CaptureTransactionId == transactionId));
    }

    private Task AddNoteAsync(Order order,
        NmiWebhookEventDescriptor descriptor,
        string eventId,
        string diagnostic)
    {
        var note = NmiRedactor.Redact(
            $"NMI webhook; event={Safe(eventId)}; type={descriptor.EventType}; " +
            $"transaction={Safe(descriptor.TransactionId)}; amount={descriptor.Amount:0.00}; " +
            $"condition={Safe(descriptor.Condition)}; result={Safe(diagnostic)}; " +
            $"occurred={descriptor.OccurredOnUtc:O}.");
        return _orderService.InsertOrderNoteAsync(new OrderNote
        {
            OrderId = order.Id,
            Note = note,
            DisplayToCustomer = false,
            CreatedOnUtc = DateTime.UtcNow
        });
    }

    private Task TryWarningAsync(string message, Exception exception = null) =>
        _log.WarningAsync(message, exception);

    private static string Safe(string value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : NmiRedactor.Redact(value.Trim());
}

internal record NmiWebhookEventDescriptor(string EventType,
    string Operation,
    NmiWebhookOutcome Outcome,
    string TransactionId,
    decimal Amount,
    string Condition,
    DateTime OccurredOnUtc)
{
    private static readonly string[] SupportedOperations = new[] { "sale", "auth", "capture", "void", "refund" };

    public static bool TryCreate(NmiWebhookEvent webhookEvent, out NmiWebhookEventDescriptor descriptor)
    {
        descriptor = null;
        if (webhookEvent is null || !Guid.TryParse(webhookEvent.EventId, out _) ||
            webhookEvent.EventId.Length > 100 || webhookEvent.EventBody is null)
            return false;

        var eventParts = webhookEvent.EventType?.Split('.');
        if (eventParts is not { Length: 3 } || eventParts[0] != "transaction" ||
            !SupportedOperations.Contains(eventParts[1], StringComparer.Ordinal))
            return false;

        var outcome = eventParts[2] switch
        {
            "success" => NmiWebhookOutcome.Success,
            "failure" => NmiWebhookOutcome.Failure,
            "unknown" => NmiWebhookOutcome.Unknown,
            _ => (NmiWebhookOutcome?)null
        };
        if (!outcome.HasValue)
            return false;

        var body = webhookEvent.EventBody;
        if (!string.Equals(body.TransactionType, "cc", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(body.TransactionId) || body.TransactionId.Length > 100 ||
            body.TransactionId.Any(char.IsWhiteSpace) || body.Action is null ||
            !string.Equals(body.Action.ActionType, eventParts[1], StringComparison.OrdinalIgnoreCase) ||
            !DateTime.TryParseExact(body.Action.Date, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var occurredOnUtc))
            return false;

        if (!decimal.TryParse(body.Action.Amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) ||
            decimal.Round(amount, 2, MidpointRounding.AwayFromZero) != amount)
            return false;
        if (eventParts[1] == "refund")
            amount = Math.Abs(amount);
        else if (amount < 0)
            return false;
        if (outcome == NmiWebhookOutcome.Success && body.Action.Success != "1")
            return false;

        descriptor = new(webhookEvent.EventType, eventParts[1], outcome.Value, body.TransactionId,
            amount, body.Condition, DateTime.SpecifyKind(occurredOnUtc, DateTimeKind.Utc));
        return true;
    }
}

internal record NmiWebhookTransition(bool Changed, string Diagnostic);

internal static class NmiWebhookStatePolicy
{
    public static bool IsTerminal(PaymentStatus status) =>
        status is PaymentStatus.Refunded or PaymentStatus.Voided;

    public static bool IsStale(DateTime occurredOnUtc, DateTime? latestProcessedOccurrenceUtc) =>
        latestProcessedOccurrenceUtc.HasValue && occurredOnUtc < latestProcessedOccurrenceUtc.Value;
}

internal enum NmiWebhookOutcome
{
    Success,
    Failure,
    Unknown
}

public enum NmiWebhookProcessingResult
{
    Accepted,
    InProgress,
    Retry,
    Malformed,
    Unauthorized
}
