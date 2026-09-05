using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Nop.Core.Domain.Payments;
using Nop.Plugin.Payments.Nmi.Domain;
using Nop.Plugin.Payments.Nmi.Services;
using Nop.Plugin.Payments.Nmi.Services.Api;
using Nop.Services.Security;
using Xunit;

namespace Nop.Plugin.Payments.Nmi.Tests;

public class NmiPaymentProcessingTests
{
    private static readonly NmiPaymentMessages Messages = new(
        "declined", "failed", "unauthorized", "ambiguous");

    [Theory]
    [InlineData(NmiTransactionMode.AuthorizeAndCapture, PaymentStatus.Paid)]
    [InlineData(NmiTransactionMode.Authorize, PaymentStatus.Authorized)]
    public void ApprovedResponseMapsToExpectedPaymentStatus(NmiTransactionMode mode, PaymentStatus expected)
    {
        var response = ApprovedResponse();

        var result = NmiPaymentResultMapper.Map(new(response, null, 200), mode, Messages);

        Assert.True(result.Success);
        Assert.Equal(expected, result.NewPaymentStatus);
        Assert.Equal("txn-123", mode == NmiTransactionMode.Authorize
            ? result.AuthorizationTransactionId
            : result.CaptureTransactionId);
        if (mode == NmiTransactionMode.Authorize)
            Assert.Equal("AUTH1", result.AuthorizationTransactionCode);
    }

    [Fact]
    public void DeclineNeverMapsToPaidOrAuthorized()
    {
        var response = ApprovedResponse() with { Response = "2", ResponseCode = "200", ResponseText = "DECLINE" };

        var result = NmiPaymentResultMapper.Map(new(response, null, 200),
            NmiTransactionMode.AuthorizeAndCapture, Messages);

        Assert.False(result.Success);
        Assert.Equal(PaymentStatus.Pending, result.NewPaymentStatus);
        Assert.Equal("declined", Assert.Single(result.Errors));
    }

    [Fact]
    public void GatewayErrorNeverMapsToPaidOrAuthorized()
    {
        var response = ApprovedResponse() with { Response = "3", ResponseCode = "300", ResponseText = "Invalid Credit Card Number REFID:1" };

        var result = NmiPaymentResultMapper.Map(new(response, null, 200),
            NmiTransactionMode.AuthorizeAndCapture, Messages);

        Assert.False(result.Success);
        Assert.Equal(PaymentStatus.Pending, result.NewPaymentStatus);
        Assert.Equal("failed", Assert.Single(result.Errors));
    }

    [Fact]
    public void AuthenticationFailureIsReportedAsUnauthorized()
    {
        var response = ApprovedResponse() with { Response = "3", ResponseCode = "300", ResponseText = "Authentication Failed", TransactionId = null };

        var result = NmiPaymentResultMapper.Map(new(response, null, 200),
            NmiTransactionMode.AuthorizeAndCapture, Messages);

        Assert.False(result.Success);
        Assert.Equal("unauthorized", Assert.Single(result.Errors));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ApprovalWithoutTransactionIdentifierIsIndeterminate(string transactionId)
    {
        var response = ApprovedResponse() with { TransactionId = transactionId };

        var result = NmiPaymentResultMapper.Map(new(response, null, 200),
            NmiTransactionMode.AuthorizeAndCapture, Messages);

        Assert.False(result.Success);
        Assert.Equal("ambiguous", Assert.Single(result.Errors));
    }

    [Fact]
    public void TransportAmbiguityIsIndeterminate()
    {
        var result = NmiPaymentResultMapper.Map(
            new(null, new NmiErrorResponse { ErrorCode = "TIMEOUT" }, 0, IsAmbiguous: true),
            NmiTransactionMode.AuthorizeAndCapture, Messages);

        Assert.False(result.Success);
        Assert.Equal("ambiguous", Assert.Single(result.Errors));
    }

    [Fact]
    public async Task SalePostsFormEncodedRequestToTransactEndpoint()
    {
        var handler = new RecordingHandler((_, _) => TextResponse(HttpStatusCode.OK,
            "response=1&responsetext=SUCCESS&authcode=123456&transactionid=txn-123&avsresponse=N&cvvresponse=M&orderid=order-guid&type=sale&response_code=100"));
        var client = CreateClient(handler);

        var result = await client.ProcessPaymentAsync(Settings(), PaymentRequest(NmiTransactionTypes.Sale));

        Assert.True(result.IsSuccess);
        Assert.Equal("https://sandbox.nmi.com/api/transact.php", handler.Uri.AbsoluteUri);
        Assert.Equal("application/x-www-form-urlencoded", handler.ContentType);
        Assert.Contains("security_key=private-key", handler.Body);
        Assert.Contains("type=sale", handler.Body);
        Assert.Contains("amount=10.25", handler.Body);
        Assert.Contains("currency=USD", handler.Body);
        Assert.Contains("payment_token=single-use-token", handler.Body);
        Assert.Contains("orderid=order-guid", handler.Body);
        Assert.Contains("ipaddress=192.0.2.1", handler.Body);
        Assert.Contains("industry=ecommerce", handler.Body);
        Assert.DoesNotContain("processor_id", handler.Body);
        Assert.Null(handler.Authorization);
        Assert.Equal("txn-123", result.Value.TransactionId);
        Assert.Equal("123456", result.Value.AuthorizationCode);
        Assert.Equal("M", result.Value.CvvResponse);
    }

    [Fact]
    public async Task AuthorizationUsesAuthTransactionType()
    {
        var handler = new RecordingHandler((_, _) => TextResponse(HttpStatusCode.OK,
            "response=1&responsetext=SUCCESS&authcode=123456&transactionid=txn-123&type=auth&response_code=100"));

        await CreateClient(handler).ProcessPaymentAsync(Settings(), PaymentRequest(NmiTransactionTypes.Authorize));

        Assert.Contains("type=auth", handler.Body);
        Assert.DoesNotContain("type=sale", handler.Body);
    }

    [Fact]
    public async Task ResponseTextIsUrlDecoded()
    {
        var handler = new RecordingHandler((_, _) => TextResponse(HttpStatusCode.OK,
            "response=3&responsetext=Invalid+Transaction+ID+%2F+Object+ID+specified%3A++REFID%3A1&authcode=&transactionid=&type=&response_code=300"));

        var result = await CreateClient(handler).ProcessPaymentAsync(Settings(), PaymentRequest(NmiTransactionTypes.Sale));

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsError);
        Assert.Equal("Invalid Transaction ID / Object ID specified:  REFID:1", result.Value.ResponseText);
    }

    [Fact]
    public async Task UnauthorizedHttpStatusIsDefinitive()
    {
        var handler = new RecordingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        var result = await CreateClient(handler).ProcessPaymentAsync(Settings(), PaymentRequest(NmiTransactionTypes.Sale));

        Assert.False(result.IsAmbiguous);
        Assert.False(result.IsSuccess);
        Assert.Equal(401, result.StatusCode);
        Assert.Equal("HTTP_401", result.Error.ErrorCode);
    }

    [Fact]
    public async Task TimeoutIsAmbiguousAndIsNotRetried()
    {
        var calls = 0;
        var handler = new RecordingHandler((_, _) =>
        {
            calls++;
            throw new TaskCanceledException("timeout");
        });

        var result = await CreateClient(handler).ProcessPaymentAsync(Settings(), PaymentRequest(NmiTransactionTypes.Sale));

        Assert.True(result.IsAmbiguous);
        Assert.Equal("TIMEOUT", result.Error.ErrorCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CallerCancellationIsPropagated()
    {
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateClient(handler).ProcessPaymentAsync(Settings(), PaymentRequest(NmiTransactionTypes.Sale),
                cancellation.Token));
    }

    [Theory]
    [InlineData("<html>maintenance</html>")]
    [InlineData("response=9&responsetext=weird")]
    public async Task MalformedSuccessResponseIsAmbiguous(string body)
    {
        var handler = new RecordingHandler((_, _) => TextResponse(HttpStatusCode.OK, body));

        var result = await CreateClient(handler).ProcessPaymentAsync(Settings(), PaymentRequest(NmiTransactionTypes.Sale));

        Assert.True(result.IsAmbiguous);
        Assert.Equal("MALFORMED_RESPONSE", result.Error.ErrorCode);
    }

    [Fact]
    public async Task EmptySuccessResponseIsAmbiguous()
    {
        var handler = new RecordingHandler((_, _) => TextResponse(HttpStatusCode.OK, string.Empty));

        var result = await CreateClient(handler).ProcessPaymentAsync(Settings(), PaymentRequest(NmiTransactionTypes.Sale));

        Assert.True(result.IsAmbiguous);
        Assert.Equal("EMPTY_RESPONSE", result.Error.ErrorCode);
    }

    [Fact]
    public async Task ServerErrorIsAmbiguousAndIsNotRetried()
    {
        var calls = 0;
        var handler = new RecordingHandler((_, _) =>
        {
            calls++;
            return TextResponse(HttpStatusCode.InternalServerError, "Internal Server Error");
        });

        var result = await CreateClient(handler).ProcessPaymentAsync(Settings(), PaymentRequest(NmiTransactionTypes.Sale));

        Assert.True(result.IsAmbiguous);
        Assert.Equal("HTTP_500", result.Error.ErrorCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ConcurrentStoresKeepGatewayOriginAndSecretIsolated()
    {
        var handler = new ConcurrentRecordingHandler();
        var client = CreateClient(handler);
        var sandbox = Settings();
        sandbox.EncryptedPrivateApiKey = "sandbox-private-key";
        var production = Settings();
        production.UseSandbox = false;
        production.EncryptedPrivateApiKey = "production-private-key";
        var custom = Settings();
        custom.UseSandbox = false;
        custom.GatewayHost = "Gateway.Example.COM";
        custom.EncryptedPrivateApiKey = "custom-private-key";

        await Task.WhenAll(
            client.ProcessPaymentAsync(sandbox, PaymentRequest(NmiTransactionTypes.Sale)),
            client.ProcessPaymentAsync(production, PaymentRequest(NmiTransactionTypes.Sale)),
            client.ProcessPaymentAsync(custom, PaymentRequest(NmiTransactionTypes.Sale)));

        Assert.Contains(handler.Requests, request =>
            request.Uri == "https://sandbox.nmi.com/api/transact.php" &&
            request.Body.Contains("security_key=sandbox-private-key"));
        Assert.Contains(handler.Requests, request =>
            request.Uri == "https://tbtech.transactiongateway.com/api/transact.php" &&
            request.Body.Contains("security_key=production-private-key"));
        Assert.Contains(handler.Requests, request =>
            request.Uri == "https://gateway.example.com/api/transact.php" &&
            request.Body.Contains("security_key=custom-private-key"));
        Assert.Equal(3, handler.Requests.Count);
    }

    private static NmiTransactionResponse ApprovedResponse() => new()
    {
        Response = "1",
        ResponseText = "SUCCESS",
        ResponseCode = "100",
        TransactionId = "txn-123",
        AuthorizationCode = "AUTH1",
        Type = "sale"
    };

    private static NmiPaymentRequest PaymentRequest(string type) => new()
    {
        TransactionType = type,
        Amount = 10.25m,
        Currency = "USD",
        PaymentToken = "single-use-token",
        Industry = "ecommerce",
        OrderId = "order-guid",
        IpAddress = "192.0.2.1"
    };

    private static NmiSettings Settings() => new()
    {
        UseSandbox = true,
        EncryptedPrivateApiKey = "private-key",
        TransactionMode = NmiTransactionMode.AuthorizeAndCapture
    };

    private static NmiHttpClient CreateClient(HttpMessageHandler handler) =>
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
        public string Authorization { get; private set; }
        public string ContentType { get; private set; }
        public string Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return await _response(request, cancellationToken);
        }
    }

    private sealed class ConcurrentRecordingHandler : HttpMessageHandler
    {
        public ConcurrentQueue<(string Uri, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Enqueue((request.RequestUri.AbsoluteUri, await request.Content.ReadAsStringAsync(cancellationToken)));
            return await TextResponse(HttpStatusCode.OK,
                "response=1&responsetext=SUCCESS&authcode=123456&transactionid=txn-123&type=sale&response_code=100");
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
