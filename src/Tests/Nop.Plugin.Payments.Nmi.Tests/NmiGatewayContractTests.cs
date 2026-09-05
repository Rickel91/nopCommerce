using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Nop.Plugin.Payments.Nmi.Domain;
using Nop.Plugin.Payments.Nmi.Services;
using Nop.Plugin.Payments.Nmi.Services.Api;
using Nop.Services.Security;
using Xunit;

namespace Nop.Plugin.Payments.Nmi.Tests;

public class NmiGatewayContractTests
{
    [Fact]
    public async Task InMemoryNmiServerCoversCompletePaymentLifecycleContract()
    {
        var server = new MockNmiHttpServer();
        var client = Client(server);
        var settings = Settings();

        var sale = await client.ProcessPaymentAsync(settings, PaymentRequest(NmiTransactionTypes.Sale));
        settings.TransactionMode = NmiTransactionMode.Authorize;
        var authorization = await client.ProcessPaymentAsync(settings, PaymentRequest(NmiTransactionTypes.Authorize));
        var capture = await client.CapturePaymentAsync(settings, "root-payment", 12.34m);
        var voidResult = await client.VoidPaymentAsync(settings, "root-payment");
        var refund = await client.RefundPaymentAsync(settings, "root-payment", 4.25m);

        Assert.All(new[] { sale, authorization, capture, voidResult, refund }, result => Assert.True(result.IsSuccess));
        Assert.Collection(server.Requests,
            request => AssertRequest(request, "type=sale", "payment_token=contract-token", "amount=12.34", "currency=USD", "orderid=contract-order"),
            request => AssertRequest(request, "type=auth", "payment_token=contract-token"),
            request => AssertRequest(request, "type=capture", "transactionid=root-payment", "amount=12.34"),
            request => AssertRequest(request, "type=void", "transactionid=root-payment"),
            request => AssertRequest(request, "type=refund", "transactionid=root-payment", "amount=4.25"));
        Assert.Equal("refund-payment", refund.Value.TransactionId);
    }

    [Fact]
    public async Task InMemoryNmiServerSeparatesDefinitiveAndAmbiguousFailures()
    {
        var server = new MockNmiHttpServer();
        var client = Client(server);

        var declined = await client.CapturePaymentAsync(Settings(), "declined", 1m);
        var gatewayError = await client.CapturePaymentAsync(Settings(), "gateway-error", 1m);
        var unavailable = await client.CapturePaymentAsync(Settings(), "unavailable", 1m);

        Assert.True(declined.IsSuccess);
        Assert.True(declined.Value.IsDeclined);
        Assert.Equal("200", declined.Value.ResponseCode);
        Assert.True(gatewayError.IsSuccess);
        Assert.True(gatewayError.Value.IsError);
        Assert.False(gatewayError.IsAmbiguous);
        Assert.True(unavailable.IsAmbiguous);
        Assert.Equal("HTTP_503", unavailable.Error.ErrorCode);
    }

    [Fact]
    public async Task CredentialValidationUsesQueryApiAndRecognisesRejectedKeys()
    {
        var server = new MockNmiHttpServer();
        var client = Client(server);

        var accepted = await client.ValidateCredentialsAsync(Settings());
        var rejected = await client.ValidateCredentialsAsync(Settings("rejected-key"));

        Assert.True(accepted);
        Assert.False(rejected);
        Assert.All(server.Requests, request =>
        {
            Assert.Equal("/api/query.php", request.Path);
            Assert.Contains("result_limit=1", request.Body);
        });
        Assert.Contains("security_key=contract-private-key", server.Requests.First().Body);
    }

    private static void AssertRequest(RecordedRequest request, params string[] bodyFragments)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/transact.php", request.Path);
        Assert.Equal("application/x-www-form-urlencoded", request.ContentType);
        Assert.Contains("security_key=contract-private-key", request.Body);
        Assert.All(bodyFragments, fragment => Assert.Contains(fragment, request.Body));
    }

    private static NmiHttpClient Client(HttpMessageHandler server) =>
        new(new HttpClient(server), new PassThroughEncryptionService());

    private static NmiSettings Settings(string privateKey = "contract-private-key") => new()
    {
        UseSandbox = true,
        EncryptedPrivateApiKey = privateKey,
        TransactionMode = NmiTransactionMode.AuthorizeAndCapture
    };

    private static NmiPaymentRequest PaymentRequest(string type) => new()
    {
        TransactionType = type,
        Amount = 12.34m,
        Currency = "USD",
        PaymentToken = "contract-token",
        OrderId = "contract-order"
    };

    private sealed class MockNmiHttpServer : HttpMessageHandler
    {
        public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Enqueue(new(request.Method, request.RequestUri.AbsolutePath,
                request.Content?.Headers.ContentType?.MediaType, body));

            var fields = NmiTransactionResponse.ParseFormBody(body ?? string.Empty);
            if (request.RequestUri.AbsolutePath.EndsWith("/query.php", StringComparison.Ordinal))
            {
                return Text(HttpStatusCode.OK, fields["security_key"] == "contract-private-key"
                    ? "<?xml version=\"1.0\"?><nm_response></nm_response>"
                    : "<?xml version=\"1.0\"?><nm_response><error_response>Invalid Username</error_response></nm_response>");
            }

            fields.TryGetValue("transactionid", out var transactionId);
            var type = fields["type"];
            return transactionId switch
            {
                "declined" => Text(HttpStatusCode.OK,
                    "response=2&responsetext=DECLINE&authcode=&transactionid=declined&type=capture&response_code=200"),
                "gateway-error" => Text(HttpStatusCode.OK,
                    "response=3&responsetext=Invalid+Transaction+ID+REFID%3A123&authcode=&transactionid=&type=capture&response_code=300"),
                "unavailable" => Text(HttpStatusCode.ServiceUnavailable, "Service Unavailable"),
                _ => Text(HttpStatusCode.OK,
                    $"response=1&responsetext=SUCCESS&authcode=123456&transactionid={(type == "refund" ? "refund-payment" : "root-payment")}" +
                    $"&avsresponse=&cvvresponse=&orderid={fields.GetValueOrDefault("orderid")}&type={type}&response_code=100")
            };
        }

        private static HttpResponseMessage Text(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/html")
        };
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

internal record RecordedRequest(HttpMethod Method, string Path, string ContentType, string Body);
