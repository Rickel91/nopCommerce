using System.Net;
using System.Text;
using Nop.Core.Domain.Orders;
using Nop.Core.Domain.Payments;
using Nop.Plugin.Payments.Nmi.Services;
using Nop.Plugin.Payments.Nmi.Services.Api;
using Nop.Services.Payments;
using Nop.Services.Security;
using Xunit;

namespace Nop.Plugin.Payments.Nmi.Tests;

public class NmiOrderOperationTests
{
    [Fact]
    public void AuthorizedOrderCanBeCapturedOnce()
    {
        var order = Order(PaymentStatus.Authorized);
        order.AuthorizationTransactionId = "root-auth";

        Assert.Equal(NmiOperationValidationError.None, NmiOrderOperationValidator.ValidateCapture(order));

        order.PaymentStatus = PaymentStatus.Paid;
        Assert.Equal(NmiOperationValidationError.InvalidState, NmiOrderOperationValidator.ValidateCapture(order));
    }

    [Fact]
    public void VoidUsesStableRootForAuthorizedAndPaidOrders()
    {
        var authorized = Order(PaymentStatus.Authorized);
        authorized.AuthorizationTransactionId = "root-auth";
        var paid = Order(PaymentStatus.Paid);
        paid.CaptureTransactionId = "root-sale";

        Assert.Equal(NmiOperationValidationError.None,
            NmiOrderOperationValidator.ValidateVoid(authorized, out var authorizationRoot));
        Assert.Equal("root-auth", authorizationRoot);
        Assert.Equal(NmiOperationValidationError.None,
            NmiOrderOperationValidator.ValidateVoid(paid, out var saleRoot));
        Assert.Equal("root-sale", saleRoot);
    }

    [Theory]
    [InlineData(25, 0, 10, 0)]
    [InlineData(25, 10, 15, 0)]
    [InlineData(25, 10, 15.01, 2)]
    [InlineData(25, 25, 1, 1)]
    [InlineData(25, 0, 0, 2)]
    public void RefundCannotExceedTrustedRemainingAmount(decimal total,
        decimal refunded,
        decimal requested,
        int expected)
    {
        var order = Order(refunded == total ? PaymentStatus.Refunded : refunded > 0
            ? PaymentStatus.PartiallyRefunded
            : PaymentStatus.Paid);
        order.OrderTotal = total;
        order.RefundedAmount = refunded;
        order.CaptureTransactionId = "root-payment";

        var actual = NmiOrderOperationValidator.ValidateRefund(order, requested, out var remaining);

        Assert.Equal((NmiOperationValidationError)expected, actual);
        Assert.Equal(total - refunded, remaining);
    }

    [Fact]
    public void ApprovedRefundRetainsRootAndNewRefundTransactionIdentifiers()
    {
        var response = new NmiTransactionResponse
        {
            Response = "1",
            ResponseCode = "100",
            ResponseText = "SUCCESS",
            TransactionId = "refund-txn",
            Type = "refund"
        };

        var outcome = NmiOrderOperationMapper.Map(new(response, null, 200), "refund", "root-payment");

        Assert.True(outcome.Success);
        Assert.Equal("root-payment", outcome.RootTransactionId);
        Assert.Equal("refund-txn", outcome.ActionId);
    }

    [Fact]
    public void ApprovedCaptureEchoingRootHasNoSeparateActionIdentifier()
    {
        var response = new NmiTransactionResponse
        {
            Response = "1",
            ResponseCode = "100",
            TransactionId = "root-payment",
            Type = "capture"
        };

        var outcome = NmiOrderOperationMapper.Map(new(response, null, 200), "capture", "root-payment");

        Assert.True(outcome.Success);
        Assert.Equal("root-payment", outcome.RootTransactionId);
        Assert.Null(outcome.ActionId);
    }

    [Theory]
    [InlineData("2", true, false)]
    [InlineData("3", false, false)]
    [InlineData("unknown", false, true)]
    public void FailedActionsNeverAdvancePaymentState(string responseCode, bool declined, bool ambiguous)
    {
        var response = new NmiTransactionResponse { TransactionId = "root-payment", Response = responseCode };

        var outcome = NmiOrderOperationMapper.Map(new(response, null, 200), "capture");

        Assert.False(outcome.Success);
        Assert.Equal(declined, outcome.Declined);
        Assert.Equal(ambiguous, outcome.Ambiguous);
    }

    [Fact]
    public void AuthenticationFailureIsReportedAsUnauthorized()
    {
        var response = new NmiTransactionResponse
        {
            Response = "3",
            ResponseCode = "300",
            ResponseText = "Authentication Failed"
        };

        var outcome = NmiOrderOperationMapper.Map(new(response, null, 200), "void", "root-payment");

        Assert.False(outcome.Success);
        Assert.True(outcome.Unauthorized);
        Assert.False(outcome.Ambiguous);
    }

    [Theory]
    [InlineData(401, true)]
    [InlineData(400, false)]
    public void DefinitiveHttpFailureNeverAdvancesState(int statusCode, bool unauthorized)
    {
        var apiResult = new NmiApiResult<NmiTransactionResponse>(null,
            new NmiErrorResponse { ErrorCode = "E_FAILURE" }, statusCode);

        var outcome = NmiOrderOperationMapper.Map(apiResult, "capture");

        Assert.False(outcome.Success);
        Assert.False(outcome.Ambiguous);
        Assert.Equal(unauthorized, outcome.Unauthorized);
        Assert.Contains("E_FAILURE", outcome.Diagnostic);
    }

    [Fact]
    public void TransportAmbiguityNeverAdvancesState()
    {
        var apiResult = new NmiApiResult<NmiTransactionResponse>(null,
            new NmiErrorResponse { ErrorCode = "TIMEOUT" }, 0, IsAmbiguous: true);

        var outcome = NmiOrderOperationMapper.Map(apiResult, "capture");

        Assert.False(outcome.Success);
        Assert.True(outcome.Ambiguous);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ApprovalWithoutTransactionIdentifierIsAmbiguous(string transactionId)
    {
        var response = new NmiTransactionResponse { TransactionId = transactionId, Response = "1" };

        var outcome = NmiOrderOperationMapper.Map(new(response, null, 200), "capture");

        Assert.False(outcome.Success);
        Assert.True(outcome.Ambiguous);
    }

    [Theory]
    [InlineData("Transaction is not settled REFID:1", true)]
    [InlineData("Refund amount may not exceed the transaction balance REFID:2", false)]
    [InlineData("Transaction has not settled yet", true)]
    [InlineData("Transaction already settled", false)]
    [InlineData(null, false)]
    public void UnsettledTransactionTextIsRecognised(string text, bool expected) =>
        Assert.Equal(expected, NmiOrderOperationMapper.IsUnsettledTransactionText(text));

    [Fact]
    public async Task FullRefundOfUnsettledSaleFallsBackToVoidOfTheSameTransaction()
    {
        var handler = new ScriptedHandler(
            "response=3&responsetext=Transaction+is+not+settled+REFID%3A1&authcode=&transactionid=&type=refund&response_code=300",
            "response=1&responsetext=Transaction+Void+Successful&authcode=123456&transactionid=root-sale&type=void&response_code=100");
        var (service, notes) = Service(handler);
        var order = Order(PaymentStatus.Paid);
        order.CaptureTransactionId = "root-sale";

        var result = await service.RefundAsync(Settings(), new RefundPaymentRequest { Order = order, AmountToRefund = 25m });

        Assert.True(result.Success);
        Assert.Equal(PaymentStatus.Voided, result.NewPaymentStatus);
        Assert.Collection(handler.Bodies,
            body => Assert.Equal("security_key=private-key&type=refund&transactionid=root-sale&amount=25.00", body),
            body => Assert.Equal("security_key=private-key&type=void&transactionid=root-sale", body));
        Assert.Contains(notes, note => note.StartsWith("NMI void; amount=25.00; root_transaction=root-sale", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("voided instead", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PartialRefundOfUnsettledSaleIsRefusedWithoutVoiding()
    {
        var handler = new ScriptedHandler(
            "response=3&responsetext=Transaction+is+not+settled+REFID%3A1&authcode=&transactionid=&type=refund&response_code=300");
        var (service, _) = Service(handler);
        var order = Order(PaymentStatus.Paid);
        order.CaptureTransactionId = "root-sale";

        var result = await service.RefundAsync(Settings(), new RefundPaymentRequest { Order = order, AmountToRefund = 10m });

        Assert.False(result.Success);
        Assert.Single(handler.Bodies);
        Assert.Equal("localized:Operations.UnsettledPartialRefund", Assert.Single(result.Errors));
    }

    [Fact]
    public async Task OtherRefundErrorsDoNotTriggerAVoid()
    {
        var handler = new ScriptedHandler(
            "response=3&responsetext=Refund+amount+may+not+exceed+the+transaction+balance+REFID%3A2&authcode=&transactionid=&type=refund&response_code=300");
        var (service, _) = Service(handler);
        var order = Order(PaymentStatus.Paid);
        order.CaptureTransactionId = "root-sale";

        var result = await service.RefundAsync(Settings(), new RefundPaymentRequest { Order = order, AmountToRefund = 25m });

        Assert.False(result.Success);
        Assert.Single(handler.Bodies);
        Assert.Equal("localized:Operations.Failed", Assert.Single(result.Errors));
    }

    private static (NmiOrderOperationService Service, List<string> Notes) Service(HttpMessageHandler handler)
    {
        var notes = new List<string>();
        var localization = TestInterfaceProxy.Create<Nop.Services.Localization.ILocalizationService>((method, args) =>
            method.Name == nameof(Nop.Services.Localization.ILocalizationService.GetResourceAsync)
                ? Task.FromResult("localized:" + ((string)args[0]).Replace(NmiDefaults.LocaleResourcePrefix + ".", string.Empty))
                : throw new NotSupportedException(method.Name));
        var orders = TestInterfaceProxy.Create<Nop.Services.Orders.IOrderService>((method, args) =>
        {
            if (method.Name != nameof(Nop.Services.Orders.IOrderService.InsertOrderNoteAsync))
                throw new NotSupportedException(method.Name);
            notes.Add(((OrderNote)args[0]).Note);
            return Task.CompletedTask;
        });
        var stores = TestInterfaceProxy.Create<Nop.Services.Stores.IStoreService>((method, _) =>
            throw new NotSupportedException(method.Name));
        var service = new NmiOrderOperationService(Client(handler), localization, NmiLogger.None, orders, stores);
        return (service, notes);
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;

        public ScriptedHandler(params string[] responses) => _responses = new Queue<string>(responses);

        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return await TextResponse(HttpStatusCode.OK, _responses.Count > 0 ? _responses.Dequeue() : "response=3&responsetext=unexpected+call");
        }
    }

    [Fact]
    public async Task CapturePostsTrustedAmountToRootTransaction()
    {
        var handler = SuccessfulHandler();

        await Client(handler).CapturePaymentAsync(Settings(), "root-auth", 25.50m);

        Assert.EndsWith("/api/transact.php", handler.Uri.AbsoluteUri);
        Assert.Equal("security_key=private-key&type=capture&transactionid=root-auth&amount=25.50", handler.Body);
    }

    [Fact]
    public async Task RefundPostsTrustedAmountToRootTransaction()
    {
        var handler = SuccessfulHandler();

        await Client(handler).RefundPaymentAsync(Settings(), "root-payment", 7.25m);

        Assert.EndsWith("/api/transact.php", handler.Uri.AbsoluteUri);
        Assert.Equal("security_key=private-key&type=refund&transactionid=root-payment&amount=7.25", handler.Body);
    }

    [Fact]
    public async Task VoidPostsOnlyTypeAndTransactionIdentifier()
    {
        var handler = SuccessfulHandler();

        await Client(handler).VoidPaymentAsync(Settings(), "root-auth");

        Assert.EndsWith("/api/transact.php", handler.Uri.AbsoluteUri);
        Assert.Equal("security_key=private-key&type=void&transactionid=root-auth", handler.Body);
    }

    [Fact]
    public async Task FollowOnTimeoutIsAmbiguousAndNotRetried()
    {
        var calls = 0;
        var handler = new RecordingHandler((_, _) =>
        {
            calls++;
            throw new TaskCanceledException("timeout");
        });

        var result = await Client(handler).RefundPaymentAsync(Settings(), "root-payment", 5m);

        Assert.True(result.IsAmbiguous);
        Assert.Equal(1, calls);
    }

    private static Order Order(PaymentStatus status) => new()
    {
        Id = 42,
        OrderTotal = 25m,
        PaymentStatus = status
    };

    private static NmiSettings Settings() => new()
    {
        UseSandbox = true,
        EncryptedPrivateApiKey = "private-key"
    };

    private static RecordingHandler SuccessfulHandler() => new((_, _) =>
        TextResponse(HttpStatusCode.OK,
            "response=1&responsetext=SUCCESS&authcode=123456&transactionid=root-payment&type=capture&response_code=100"));

    private static NmiHttpClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler), new PassThroughEncryptionService());

    private static Task<HttpResponseMessage> TextResponse(HttpStatusCode status, string body) =>
        Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/html")
        });

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _response;

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) =>
            _response = response;

        public Uri Uri { get; private set; }
        public string Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return await _response(request, cancellationToken);
        }
    }

    private sealed class PassThroughEncryptionService : IEncryptionService
    {
        public string DecryptText(string cipherText, string encryptionPrivateKey = "") => cipherText;
        public string EncryptText(string plainText, string encryptionPrivateKey = "") => plainText;
        public string CreateSaltKey(int size) => throw new NotSupportedException();
        public string CreatePasswordHash(string password, string saltKey, string passwordFormat) =>
            throw new NotSupportedException();
    }
}
