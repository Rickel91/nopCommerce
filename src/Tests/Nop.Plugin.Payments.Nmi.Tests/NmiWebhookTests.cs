using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nop.Core.Domain.Orders;
using Nop.Core.Domain.Payments;
using Nop.Plugin.Payments.Nmi.Controllers;
using Nop.Plugin.Payments.Nmi.Domain;
using Nop.Plugin.Payments.Nmi.Services;
using Nop.Plugin.Payments.Nmi.Services.Api;
using Xunit;

namespace Nop.Plugin.Payments.Nmi.Tests;

public class NmiWebhookTests
{
    private static readonly DateTimeOffset SignedAt =
        new(2026, 7, 18, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExactRawFixtureSignatureIsAccepted()
    {
        var body = Fixture();
        var header = Sign(body, "signing-key", SignedAt.ToUnixTimeSeconds());

        var result = NmiWebhookSignatureVerifier.Verify(body, header, ["signing-key"], SignedAt);

        Assert.Equal(NmiWebhookSignatureVerification.Valid, result);
    }

    [Fact]
    public void TamperedRawFixtureSignatureIsRejected()
    {
        var body = Fixture();
        var header = Sign(body, "signing-key", SignedAt.ToUnixTimeSeconds());
        body[^2] ^= 1;

        var result = NmiWebhookSignatureVerifier.Verify(body, header, ["signing-key"], SignedAt);

        Assert.Equal(NmiWebhookSignatureVerification.Invalid, result);
    }

    [Fact]
    public void ValidSignatureAuthorizesOnlyStoresUsingTheMatchingKey()
    {
        var body = Fixture();
        var header = Sign(body, "store-one-key", SignedAt.ToUnixTimeSeconds());

        var result = NmiWebhookSignatureVerifier.VerifyForStores(body, header,
        [
            new NmiWebhookSigningKey(1, "store-one-key"),
            new NmiWebhookSigningKey(2, "store-two-key")
        ], SignedAt);

        Assert.Equal(NmiWebhookSignatureVerification.Valid, result.Verification);
        Assert.Equal([1], result.AuthorizedStoreIds);
    }

    [Fact]
    public void ExpiredSignatureIsRejected()
    {
        var body = Fixture();
        var header = Sign(body, "signing-key", SignedAt.ToUnixTimeSeconds());

        var result = NmiWebhookSignatureVerifier.Verify(body, header, ["signing-key"],
            SignedAt.AddMinutes(6));

        Assert.Equal(NmiWebhookSignatureVerification.Expired, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("s=abc,t=123")]
    [InlineData("t=1784404800,s=not-hex")]
    [InlineData("t=1784404800,s=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa,extra=1")]
    public void MalformedSignatureHeaderIsRejected(string header)
    {
        var result = NmiWebhookSignatureVerifier.Verify(Fixture(), header, ["signing-key"], SignedAt);

        Assert.Equal(NmiWebhookSignatureVerification.Malformed, result);
    }

    [Fact]
    public void RawFixtureParsesSupportedTransactionMetadata()
    {
        var webhookEvent = JsonSerializer.Deserialize<NmiWebhookEvent>(Fixture(), NmiHttpClient.SerializerOptions);

        var valid = NmiWebhookEventDescriptor.TryCreate(webhookEvent, out var descriptor);

        Assert.True(valid);
        Assert.Equal("sale", descriptor.Operation);
        Assert.Equal(NmiWebhookOutcome.Success, descriptor.Outcome);
        Assert.Equal(60.49m, descriptor.Amount);
        Assert.Equal(DateTimeKind.Utc, descriptor.OccurredOnUtc.Kind);
    }

    [Fact]
    public void RefundFixtureNormalizesNmiNegativeActionAmount()
    {
        var webhookEvent = JsonSerializer.Deserialize<NmiWebhookEvent>(
            Fixture("transaction-refund-success.json"), NmiHttpClient.SerializerOptions);

        var valid = NmiWebhookEventDescriptor.TryCreate(webhookEvent, out var descriptor);

        Assert.True(valid);
        Assert.Equal("refund", descriptor.Operation);
        Assert.Equal(1.50m, descriptor.Amount);
    }

    [Theory]
    [InlineData("transaction.sale.failure", 1)]
    [InlineData("transaction.sale.unknown", 2)]
    public void FailureAndUnknownEventsRemainDiagnosticOnly(string eventType, int expected)
    {
        var original = JsonSerializer.Deserialize<NmiWebhookEvent>(Fixture(), NmiHttpClient.SerializerOptions);
        var webhookEvent = original with { EventId = Guid.NewGuid().ToString(), EventType = eventType };

        var valid = NmiWebhookEventDescriptor.TryCreate(webhookEvent, out var descriptor);

        Assert.True(valid);
        Assert.Equal((NmiWebhookOutcome)expected, descriptor.Outcome);
    }

    [Fact]
    public void NonCardTransactionEventIsOutsidePluginScope()
    {
        var original = JsonSerializer.Deserialize<NmiWebhookEvent>(Fixture(), NmiHttpClient.SerializerOptions);
        var webhookEvent = original with
        {
            EventId = Guid.NewGuid().ToString(),
            EventBody = original.EventBody with { TransactionType = "ck" }
        };

        Assert.False(NmiWebhookEventDescriptor.TryCreate(webhookEvent, out _));
    }

    [Theory]
    [InlineData("transaction.sale.Success")]
    [InlineData("transaction.sale.0")]
    public void NonCanonicalOutcomeNamesAreRejected(string eventType)
    {
        var original = JsonSerializer.Deserialize<NmiWebhookEvent>(Fixture(), NmiHttpClient.SerializerOptions);
        var webhookEvent = original with { EventId = Guid.NewGuid().ToString(), EventType = eventType };

        Assert.False(NmiWebhookEventDescriptor.TryCreate(webhookEvent, out _));
    }

    [Fact]
    public void OnlyWebhookActionExplicitlyAllowsAnonymousAntiforgeryFreePosts()
    {
        var action = typeof(NmiWebhookController).GetMethod(nameof(NmiWebhookController.Transactions));

        Assert.NotNull(action.GetCustomAttributes(typeof(AllowAnonymousAttribute), false).SingleOrDefault());
        Assert.NotNull(action.GetCustomAttributes(typeof(IgnoreAntiforgeryTokenAttribute), false).SingleOrDefault());
        Assert.Null(typeof(NmiWebhookController)
            .GetCustomAttributes(typeof(IgnoreAntiforgeryTokenAttribute), false).SingleOrDefault());
    }

    [Fact]
    public async Task ConcurrentDeliveryAcquiresEventOnlyOnce()
    {
        var repository = new InMemoryWebhookRecordRepository();
        var service = new NmiWebhookRecordService(repository);
        var eventId = Guid.NewGuid().ToString();

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            service.AcquireAsync(Record(eventId))));

        Assert.Single(results, result => result.Status == NmiWebhookAcquireStatus.Acquired);
        Assert.Equal(19, results.Count(result => result.Status == NmiWebhookAcquireStatus.InProgress));
    }

    [Fact]
    public async Task CompletedEventReplayHasNoAdditionalEffect()
    {
        var repository = new InMemoryWebhookRecordRepository();
        var service = new NmiWebhookRecordService(repository);
        var eventId = Guid.NewGuid().ToString();
        var first = await service.AcquireAsync(Record(eventId));
        await service.CompleteAsync(first.Record, NmiWebhookProcessingState.Processed, "done", 42);

        var replay = await service.AcquireAsync(Record(eventId));

        Assert.Equal(NmiWebhookAcquireStatus.Completed, replay.Status);
        Assert.Equal(1, repository.Count);
    }

    [Fact]
    public void OlderEventIsStaleButEqualTimestampIsNot()
    {
        var latest = new DateTime(2026, 7, 18, 20, 0, 1, DateTimeKind.Utc);

        Assert.True(NmiWebhookStatePolicy.IsStale(latest.AddSeconds(-1), latest));
        Assert.False(NmiWebhookStatePolicy.IsStale(latest, latest));
        Assert.False(NmiWebhookStatePolicy.IsStale(latest, null));
    }

    [Theory]
    [InlineData(PaymentStatus.Refunded, true)]
    [InlineData(PaymentStatus.Voided, true)]
    [InlineData(PaymentStatus.Paid, false)]
    [InlineData(PaymentStatus.Authorized, false)]
    public void TerminalStatePolicyPreventsRegression(PaymentStatus status, bool expected)
    {
        Assert.Equal(expected, NmiWebhookStatePolicy.IsTerminal(status));
    }

    [Fact]
    public async Task CleanupRemovesOnlyRecordsOutsideRetentionWindow()
    {
        var repository = new InMemoryWebhookRecordRepository();
        var service = new NmiWebhookRecordService(repository);
        var old = Record(Guid.NewGuid().ToString());
        old.ReceivedOnUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var current = Record(Guid.NewGuid().ToString());
        current.ReceivedOnUtc = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        await repository.InsertAsync(old);
        await repository.InsertAsync(current);

        var deleted = await service.DeleteExpiredAsync(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(1, deleted);
        Assert.Equal(1, repository.Count);
    }

    [Fact]
    public async Task MissingOrderRemainsRetryableAndDoesNotGuessState()
    {
        var repository = new InMemoryWebhookRecordRepository();
        var service = new NmiWebhookRecordService(repository);
        var processor = new MissingOrderWebhookProcessor(service);
        var webhookEvent = JsonSerializer.Deserialize<NmiWebhookEvent>(Fixture(), NmiHttpClient.SerializerOptions);

        var result = await processor.ProcessAsync(webhookEvent);

        Assert.Equal(NmiWebhookProcessingResult.Retry, result);
        var record = await repository.FindByEventIdAsync(webhookEvent.EventId);
        Assert.Equal(NmiWebhookProcessingState.Failed, record.ProcessingState);
        Assert.Equal("order_not_found", record.Diagnostic);
    }

    private static byte[] Fixture(string name = "transaction-sale-success.json") =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static string Sign(byte[] body, string key, long timestamp)
    {
        var prefix = Encoding.UTF8.GetBytes($"{timestamp}.");
        var payload = new byte[prefix.Length + body.Length];
        Buffer.BlockCopy(prefix, 0, payload, 0, prefix.Length);
        Buffer.BlockCopy(body, 0, payload, prefix.Length, body.Length);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return $"t={timestamp},s={Convert.ToHexString(hmac.ComputeHash(payload)).ToLowerInvariant()}";
    }

    private static NmiWebhookRecord Record(string eventId) => new()
    {
        EventId = eventId,
        EventType = "transaction.sale.success",
        TransactionId = "1234560000",
        ProcessingState = NmiWebhookProcessingState.Processing,
        ReceivedOnUtc = DateTime.UtcNow,
        OccurredOnUtc = DateTime.UtcNow
    };

    private sealed class MissingOrderWebhookProcessor : NmiWebhookProcessor
    {
        public MissingOrderWebhookProcessor(NmiWebhookRecordService recordService)
            : base(null, null, null, null, null, recordService)
        {
        }

        protected override Task<Order> FindOrderAsync(string orderCorrelation, string transactionId) =>
            Task.FromResult<Order>(null);
    }

    private sealed class InMemoryWebhookRecordRepository : INmiWebhookRecordRepository
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
                .Max(record => record.OccurredOnUtc);
            return Task.FromResult(latest);
        }

        public Task<int> DeleteExpiredAsync(DateTime cutoffUtc)
        {
            var deleted = 0;
            foreach (var record in _records.Values.Where(record => record.ReceivedOnUtc < cutoffUtc))
            {
                if (_records.TryRemove(record.EventId, out _))
                    deleted++;
            }
            return Task.FromResult(deleted);
        }
    }
}
