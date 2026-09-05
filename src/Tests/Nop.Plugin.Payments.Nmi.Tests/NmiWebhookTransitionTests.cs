using System.Collections.Concurrent;
using Nop.Core.Domain.Orders;
using Nop.Core.Domain.Payments;
using Nop.Plugin.Payments.Nmi.Domain;
using Nop.Plugin.Payments.Nmi.Services;
using Nop.Plugin.Payments.Nmi.Services.Api;
using Nop.Services.Configuration;
using Nop.Services.Orders;
using Xunit;

namespace Nop.Plugin.Payments.Nmi.Tests;

public class NmiWebhookTransitionTests
{
    [Theory]
    [InlineData("sale", PaymentStatus.Pending, PaymentStatus.Paid)]
    [InlineData("auth", PaymentStatus.Pending, PaymentStatus.Authorized)]
    [InlineData("capture", PaymentStatus.Authorized, PaymentStatus.Paid)]
    [InlineData("void", PaymentStatus.Paid, PaymentStatus.Voided)]
    [InlineData("refund", PaymentStatus.Paid, PaymentStatus.Refunded)]
    public async Task SuccessfulEventsApplyExpectedMonotonicState(string operation,
        PaymentStatus initial,
        PaymentStatus expected)
    {
        var order = Order(initial);
        var harness = new WebhookHarness(order);
        var webhookEvent = Event(operation, "success", "20260718200000");

        var result = await harness.Processor.ProcessAsync(webhookEvent, new HashSet<int> { 7 });

        Assert.Equal(NmiWebhookProcessingResult.Accepted, result);
        Assert.Equal(expected, order.PaymentStatus);
        Assert.Equal(1, harness.OrderUpdates);
        Assert.Equal(1, harness.OrderStatusChecks);
        var record = await harness.Repository.FindByEventIdAsync(webhookEvent.EventId);
        Assert.Equal(NmiWebhookProcessingState.Processed, record.ProcessingState);
        if (operation == "refund")
            Assert.Equal(order.OrderTotal, order.RefundedAmount);
        if (operation == "sale")
            Assert.Equal("root-payment", order.CaptureTransactionId);
        if (operation == "auth")
            Assert.Equal("root-payment", order.AuthorizationTransactionId);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("unknown")]
    public async Task NonSuccessEventNeverAdvancesPaymentState(string outcome)
    {
        var order = Order(PaymentStatus.Pending);
        var harness = new WebhookHarness(order);

        var result = await harness.Processor.ProcessAsync(Event("sale", outcome, "20260718200000"),
            new HashSet<int> { 7 });

        Assert.Equal(NmiWebhookProcessingResult.Accepted, result);
        Assert.Equal(PaymentStatus.Pending, order.PaymentStatus);
        Assert.Equal(0, harness.OrderUpdates);
        Assert.Equal(0, harness.OrderStatusChecks);
    }

    [Fact]
    public async Task LaterSuccessReconcilesAnEarlierUnknownOutcomeExactlyOnce()
    {
        var order = Order(PaymentStatus.Pending);
        var harness = new WebhookHarness(order);
        var unknown = Event("sale", "unknown", "20260718200000");
        var success = Event("sale", "success", "20260718200001");

        Assert.Equal(NmiWebhookProcessingResult.Accepted,
            await harness.Processor.ProcessAsync(unknown, new HashSet<int> { 7 }));
        Assert.Equal(PaymentStatus.Pending, order.PaymentStatus);
        Assert.Equal(NmiWebhookProcessingResult.Accepted,
            await harness.Processor.ProcessAsync(success, new HashSet<int> { 7 }));
        Assert.Equal(NmiWebhookProcessingResult.Accepted,
            await harness.Processor.ProcessAsync(success, new HashSet<int> { 7 }));

        Assert.Equal(PaymentStatus.Paid, order.PaymentStatus);
        Assert.Equal(1, harness.OrderUpdates);
        Assert.Equal(1, harness.OrderStatusChecks);
        Assert.Equal(2, harness.Repository.Count);
    }

    [Fact]
    public async Task SigningKeyFromAnotherStoreCannotPersistOrChangeEvent()
    {
        var order = Order(PaymentStatus.Pending);
        var harness = new WebhookHarness(order);
        var webhookEvent = Event("sale", "success", "20260718200000");

        var result = await harness.Processor.ProcessAsync(webhookEvent, new HashSet<int> { 8 });

        Assert.Equal(NmiWebhookProcessingResult.Unauthorized, result);
        Assert.Equal(PaymentStatus.Pending, order.PaymentStatus);
        Assert.Equal(0, harness.OrderUpdates);
        Assert.Equal(0, harness.Repository.Count);
    }

    private static Order Order(PaymentStatus status) => new()
    {
        Id = 42,
        StoreId = 7,
        OrderGuid = Guid.Parse("a9c3d7f3-cbfe-43ca-8d43-55ff4683c451"),
        OrderTotal = 10m,
        PaymentStatus = status,
        PaymentMethodSystemName = NmiDefaults.SystemName,
        AuthorizationTransactionId = status == PaymentStatus.Authorized ? "root-payment" : null,
        CaptureTransactionId = status == PaymentStatus.Paid ? "root-payment" : null
    };

    private static NmiWebhookEvent Event(string operation, string outcome, string date) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        EventType = $"transaction.{operation}.{outcome}",
        EventBody = new NmiWebhookEventBody
        {
            TransactionId = "root-payment",
            TransactionType = "cc",
            Condition = outcome,
            OrderId = "a9c3d7f3cbfe43ca8d4355ff4683c451",
            Currency = "USD",
            RequestedAmount = "10.00",
            Features = new NmiWebhookFeatures { IsTestMode = true },
            Action = new NmiWebhookAction
            {
                Amount = operation == "refund" ? "-10.00" : "10.00",
                ActionType = operation,
                Date = date,
                Success = outcome == "success" ? "1" : "0",
                ResponseCode = outcome == "success" ? "100" : "300",
                ResponseText = outcome
            }
        }
    };

    private sealed class WebhookHarness
    {
        private readonly Order _order;

        public WebhookHarness(Order order)
        {
            _order = order;
            Repository = new InMemoryWebhookRepository();
            var recordService = new NmiWebhookRecordService(Repository);
            var orderService = TestInterfaceProxy.Create<IOrderService>((method, args) => method.Name switch
            {
                nameof(IOrderService.UpdateOrderAsync) => UpdateOrder(),
                nameof(IOrderService.GetOrderNotesByOrderIdAsync) =>
                    Task.FromResult<IList<OrderNote>>([]),
                nameof(IOrderService.InsertOrderNoteAsync) => InsertNote((OrderNote)args[0]),
                _ => throw new NotSupportedException(method.Name)
            });
            var orderProcessingService = TestInterfaceProxy.Create<IOrderProcessingService>((method, _) =>
                method.Name == nameof(IOrderProcessingService.CheckOrderStatusAsync)
                    ? CheckStatus()
                    : throw new NotSupportedException(method.Name));
            var settingService = TestInterfaceProxy.Create<ISettingService>((method, _) =>
                method.Name == nameof(ISettingService.LoadSettingAsync) && method.IsGenericMethod
                    ? Task.FromResult(new NmiSettings { UseSandbox = true })
                    : throw new NotSupportedException(method.Name));

            Processor = new TestableWebhookProcessor(order, orderProcessingService, orderService,
                settingService, recordService);
        }

        public TestableWebhookProcessor Processor { get; }
        public InMemoryWebhookRepository Repository { get; }
        public int OrderUpdates { get; private set; }
        public int OrderStatusChecks { get; private set; }
        public IList<OrderNote> Notes { get; } = [];

        private Task UpdateOrder()
        {
            OrderUpdates++;
            return Task.CompletedTask;
        }

        private Task CheckStatus()
        {
            OrderStatusChecks++;
            return Task.CompletedTask;
        }

        private Task InsertNote(OrderNote note)
        {
            Notes.Add(note);
            return Task.CompletedTask;
        }
    }

    private sealed class TestableWebhookProcessor : NmiWebhookProcessor
    {
        private readonly Order _order;

        public TestableWebhookProcessor(Order order,
            IOrderProcessingService orderProcessingService,
            IOrderService orderService,
            ISettingService settingService,
            NmiWebhookRecordService recordService)
            : base(null, null, orderProcessingService, orderService, settingService, recordService)
        {
            _order = order;
        }

        protected override Task<Order> FindOrderAsync(string orderCorrelation, string transactionId) =>
            Task.FromResult(_order);
    }

    public sealed class InMemoryWebhookRepository : INmiWebhookRecordRepository
    {
        private readonly ConcurrentDictionary<string, NmiWebhookRecord> _records = new(StringComparer.Ordinal);
        private int _nextId;

        public int Count => _records.Count;

        public Task<NmiWebhookRecord> FindByEventIdAsync(string eventId)
        {
            _records.TryGetValue(eventId, out var record);
            return Task.FromResult(record);
        }

        public Task InsertAsync(NmiWebhookRecord record)
        {
            record.Id = Interlocked.Increment(ref _nextId);
            if (!_records.TryAdd(record.EventId, record))
                throw new InvalidOperationException("Duplicate event ID.");
            return Task.CompletedTask;
        }

        public Task UpdateAsync(NmiWebhookRecord record)
        {
            _records[record.EventId] = record;
            return Task.CompletedTask;
        }

        public Task<DateTime?> GetLatestProcessedOccurrenceAsync(int orderId, int excludingRecordId)
        {
            var latest = _records.Values
                .Where(record => record.OrderId == orderId && record.Id != excludingRecordId &&
                                 record.ProcessingState == NmiWebhookProcessingState.Processed)
                .Select(record => record.OccurredOnUtc)
                .OrderByDescending(value => value)
                .FirstOrDefault();
            return Task.FromResult(latest);
        }

        public Task<int> DeleteExpiredAsync(DateTime cutoffUtc) => Task.FromResult(0);
    }
}
