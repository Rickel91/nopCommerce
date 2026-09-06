using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Nop.Core;
using Nop.Core.Domain.Customers;
using Nop.Core.Domain.Payments;
using Nop.Core.Domain.Stores;
using Nop.Services.Common;
using Nop.Services.Payments;
using Nop.Plugin.Payments.Nmi.Models;
using Nop.Plugin.Payments.Nmi.Services;
using Nop.Plugin.Payments.Nmi.Services.Api;
using Nop.Services.Security;
using Xunit;

namespace Nop.Plugin.Payments.Nmi.Tests;

public class NmiFoundationTests
{
    [Theory]
    [InlineData(true, null, "https://sandbox.nmi.com/")]
    [InlineData(true, "gateway.example.com", "https://sandbox.nmi.com/")]
    [InlineData(false, null, "https://tbtech.transactiongateway.com/")]
    [InlineData(false, "", "https://tbtech.transactiongateway.com/")]
    [InlineData(false, "  Gateway.Example.COM ", "https://gateway.example.com/")]
    public void EndpointSelectionUsesSandboxOrConfiguredGatewayHost(bool sandbox, string host, string expected) =>
        Assert.Equal(expected, NmiHttpClient.GetBaseUri(sandbox, host).AbsoluteUri);

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("evil.example/path")]
    [InlineData("evil.example:8443")]
    [InlineData("user@evil.example")]
    [InlineData("localhost")]
    [InlineData("evil..example")]
    [InlineData("-evil.example")]
    public void InvalidGatewayHostsAreRejected(string host)
    {
        Assert.False(NmiHttpClient.IsValidGatewayHost(host));
        Assert.Throws<ArgumentException>(() => NmiHttpClient.GetBaseUri(false, host));
    }

    [Fact]
    public void CollectJsIsLoadedFromTheConfiguredGatewayOrigin()
    {
        var sandbox = new NmiSettings { UseSandbox = true };
        var production = new NmiSettings { UseSandbox = false, GatewayHost = "tbtech.transactiongateway.com" };

        Assert.Equal("https://sandbox.nmi.com/token/Collect.js", NmiHttpClient.GetCollectJsUri(sandbox).AbsoluteUri);
        Assert.Equal("https://tbtech.transactiongateway.com/token/Collect.js",
            NmiHttpClient.GetCollectJsUri(production).AbsoluteUri);
    }

    [Theory]
    [InlineData("EnableThreeDSecure")]
    [InlineData("EnableApplePay")]
    [InlineData("EnableGooglePay")]
    public void UnsupportedFollowUpFeaturesAreNotExposedAsSettings(string propertyName)
    {
        Assert.Null(typeof(NmiSettings).GetProperty(propertyName));
        Assert.Null(typeof(ConfigurationModel).GetProperty(propertyName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("••••••••")]
    [InlineData("********")]
    [InlineData("%E2%80%A2%E2%80%A2%E2%80%A2")]
    [InlineData("????????")]
    public void ExistingSecretIsPreservedWhenSubmissionIsBlankOrMasked(string submitted)
    {
        var encryption = new FakeEncryptionService();

        var protectedValue = NmiSecretProtector.Protect(submitted, "existing-ciphertext", encryption);

        Assert.Equal("existing-ciphertext", protectedValue);
        Assert.Equal(0, encryption.EncryptCalls);
    }

    [Fact]
    public void ChangedSecretIsTrimmedAndEncrypted()
    {
        var encryption = new FakeEncryptionService();

        var protectedValue = NmiSecretProtector.Protect("  new-secret  ", "old", encryption);

        Assert.Equal("encrypted:new-secret", protectedValue);
        Assert.Equal(1, encryption.EncryptCalls);
    }

    [Fact]
    public void PaymentRequestUsesClassicApiVariableNames()
    {
        var request = new NmiPaymentRequest
        {
            Amount = 12.34m,
            Currency = "USD",
            PaymentToken = "token-value",
            DuplicateCheckSeconds = 30
        };

        var fields = request.ToFormFields();

        Assert.Contains(fields, field => field is { Key: "payment_token", Value: "token-value" });
        Assert.Contains(fields, field => field is { Key: "dup_seconds", Value: "30" });
        Assert.Contains(fields, field => field is { Key: "amount", Value: "12.34" });
        Assert.DoesNotContain(fields, field => field.Key == "PaymentToken");
    }

    [Fact]
    public void GatewayResponseParsingHandlesEncodingAndDuplicateKeys()
    {
        var parsed = NmiTransactionResponse.TryParse(
            "response=1&responsetext=SUCCESS&authcode=123456&transactionid=8558644015&avsresponse=&cvvresponse=&orderid=a%26b&type=sale&response_code=100&response=2\n",
            out var response);

        Assert.True(parsed);
        Assert.Equal("1", response.Response);
        Assert.Equal("8558644015", response.TransactionId);
        Assert.Equal("a&b", response.OrderId);
        Assert.Null(response.AvsResponse);
        Assert.True(response.IsApproved);
    }

    [Fact]
    public void SensitiveValuesAreRedacted()
    {
        const string input = "security_key: secret payment_token=tok_123 webhook_signing_key='hook-secret' " +
                             "ccnumber=4111111111111111 cvv=987";

        var redacted = NmiRedactor.Redact(input);

        Assert.DoesNotContain("secret", redacted);
        Assert.DoesNotContain("tok_123", redacted);
        Assert.DoesNotContain("hook-secret", redacted);
        Assert.DoesNotContain("4111111111111111", redacted);
        Assert.DoesNotContain("987", redacted);
        Assert.Equal(5, redacted.Split("[REDACTED]").Length - 1);
    }

    [Fact]
    public void StructuredErrorMessagesAreRedacted()
    {
        var error = new NmiErrorResponse { Message = "security_key: private-value" };

        var redacted = NmiRedactor.Redact(error);

        Assert.DoesNotContain("private-value", redacted.Message);
    }

    [Fact]
    public void TokenHandoffAcceptsExactlyOneCurrentToken()
    {
        var form = CreateForm((NmiDefaults.PaymentTokenFormKey, "  token-value  "));

        var accepted = NmiPaymentMethod.TryGetPaymentToken(form, out var token);

        Assert.True(accepted);
        Assert.Equal("token-value", token);
    }

    [Theory]
    [InlineData("")]
    [InlineData("token with spaces")]
    public void TokenHandoffRejectsInvalidTokens(string value)
    {
        var form = CreateForm((NmiDefaults.PaymentTokenFormKey, value));

        Assert.False(NmiPaymentMethod.TryGetPaymentToken(form, out _));
    }

    [Fact]
    public void TokenHandoffRejectsMultipleTokenValues()
    {
        var form = new FormCollection(new Dictionary<string, StringValues>
        {
            [NmiDefaults.PaymentTokenFormKey] = new StringValues(new[] { "token-one", "token-two" })
        });

        Assert.False(NmiPaymentMethod.TryGetPaymentToken(form, out _));
    }

    [Fact]
    public async Task PaymentInfoParksOnlyTokenOnCustomerAndKeepsCustomValuesEmpty()
    {
        var customer = new Customer { Id = 11 };
        var saved = new List<(string Key, object Value, int StoreId)>();
        var attributes = TestInterfaceProxy.Create<IGenericAttributeService>((method, args) =>
        {
            if (method.Name != nameof(IGenericAttributeService.SaveAttributeAsync))
                throw new NotSupportedException(method.Name);
            Assert.Same(customer, args[0]);
            saved.Add(((string)args[1], args[2], (int)args[3]));
            return Task.CompletedTask;
        });
        var workContext = TestInterfaceProxy.Create<IWorkContext>((method, _) =>
            method.Name == nameof(IWorkContext.GetCurrentCustomerAsync)
                ? Task.FromResult(customer)
                : throw new NotSupportedException(method.Name));
        var storeContext = TestInterfaceProxy.Create<IStoreContext>((method, _) =>
            method.Name == nameof(IStoreContext.GetCurrentStoreAsync)
                ? Task.FromResult(new Store { Id = 7 })
                : throw new NotSupportedException(method.Name));
        var method = new NmiPaymentMethod(attributes, null, null, null, storeContext, null, workContext,
            new NmiSettings(), new PaymentSettings(), null, null, null);
        var form = CreateForm(
            (NmiDefaults.PaymentTokenFormKey, "token-value"),
            ("CardNumber", "pan-must-not-be-read"),
            ("CardCode", "cvv-must-not-be-read"));

        var request = await method.GetPaymentInfoAsync(form);

        // Nothing may reach CustomValues: nopCommerce shows them on the confirm page and stores them with the order.
        Assert.Empty(request.CustomValues);
        var entry = Assert.Single(saved);
        Assert.Equal(NmiDefaults.PaymentTokenAttribute, entry.Key);
        Assert.Equal("token-value", entry.Value);
        Assert.Equal(7, entry.StoreId);
    }

    [Fact]
    public void MaskedCardMetadataMapsToNopCardFieldsWithoutExposingAPan()
    {
        var request = new ProcessPaymentRequest();
        var form = CreateForm(
            (NmiDefaults.CardLast4FormKey, "1111"),
            (NmiDefaults.CardBrandFormKey, "visa"),
            (NmiDefaults.CardExpirationFormKey, "1230"));

        NmiPaymentMethod.ApplyMaskedCardMetadata(request, form);

        Assert.Equal("************1111", request.CreditCardNumber);
        Assert.Equal("Visa", request.CreditCardType);
        Assert.Equal(12, request.CreditCardExpireMonth);
        Assert.Equal(2030, request.CreditCardExpireYear);
    }

    [Theory]
    [InlineData("4111111111111111", "visa", "1230")]
    [InlineData("11a1", "visa", "1230")]
    [InlineData("1111", "<script>", "1330")]
    public void InvalidCardMetadataIsIgnored(string last4, string brand, string expiration)
    {
        var request = new ProcessPaymentRequest();
        var form = CreateForm(
            (NmiDefaults.CardLast4FormKey, last4),
            (NmiDefaults.CardBrandFormKey, brand),
            (NmiDefaults.CardExpirationFormKey, expiration));

        NmiPaymentMethod.ApplyMaskedCardMetadata(request, form);

        Assert.True(request.CreditCardNumber is null || request.CreditCardNumber == "************1111");
        Assert.DoesNotContain("4111111111111111", request.CreditCardNumber ?? string.Empty);
        Assert.True(request.CreditCardType is null or "Visa");
        Assert.True(request.CreditCardExpireMonth is 0 or 12);
    }

    [Theory]
    [InlineData("https://evil.example/api/transact.php")]
    [InlineData("//evil.example/api/transact.php")]
    [InlineData("\\\\evil.example\\api\\transact.php")]
    [InlineData("/api/transact.php")]
    [InlineData("api/../../outside")]
    [InlineData("api/%2e%2e/%2e%2e/outside")]
    [InlineData("api/transact.php?redirect=https://evil.example")]
    [InlineData("token/Collect.js")]
    public void GatewayPathCannotEscapeFixedApiPrefix(string path)
    {
        Assert.Throws<ArgumentException>(() =>
            NmiHttpClient.BuildRequestUri(NmiDefaults.SandboxBaseUri, path));
    }

    [Theory]
    [InlineData("api/transact.php", "https://sandbox.nmi.com/api/transact.php")]
    [InlineData("api/query.php", "https://sandbox.nmi.com/api/query.php")]
    public void GatewayPathAcceptsOnlyExpectedClassicApiPaths(string path, string expected)
    {
        var uri = NmiHttpClient.BuildRequestUri(NmiDefaults.SandboxBaseUri, path);

        Assert.Equal(expected, uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("root/payment")]
    [InlineData("root%2Fpayment")]
    [InlineData("root payment")]
    public async Task UnsafeTransactionIdentifierIsRejectedBeforeHttpDispatch(string transactionId)
    {
        var client = new NmiHttpClient(new HttpClient(new NeverCalledHandler()), new FakeEncryptionService());
        var settings = new NmiSettings { EncryptedPrivateApiKey = "encrypted-key" };

        await Assert.ThrowsAsync<ArgumentException>(() => client.CapturePaymentAsync(settings, transactionId, 1m));
        await Assert.ThrowsAsync<ArgumentException>(() => client.VoidPaymentAsync(settings, transactionId));
        await Assert.ThrowsAsync<ArgumentException>(() => client.RefundPaymentAsync(settings, transactionId, 1m));
    }

    [Fact]
    public async Task ConcurrentCheckoutGuardAllowsOnlyOneActiveSubmissionPerOrder()
    {
        var guard = new NmiPaymentExecutionGuard();
        var correlation = Guid.NewGuid();
        Assert.True(guard.TryAcquire(correlation, out var lease));

        var duplicateResults = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(index => Task.Run(() => guard.TryAcquire(correlation, out _))));

        Assert.All(duplicateResults, Assert.False);
        lease.Dispose();
        Assert.True(guard.TryAcquire(correlation, out var nextLease));
        nextLease.Dispose();
    }

    private static FormCollection CreateForm(params (string Key, string Value)[] values) =>
        new(values.ToDictionary(item => item.Key, item => new StringValues(item.Value)));

    private sealed class FakeEncryptionService : IEncryptionService
    {
        public int EncryptCalls { get; private set; }

        public string EncryptText(string plainText, string encryptionPrivateKey = "")
        {
            EncryptCalls++;
            return $"encrypted:{plainText}";
        }

        public string DecryptText(string cipherText, string encryptionPrivateKey = "") => cipherText;
        public string CreateSaltKey(int size) => throw new NotSupportedException();
        public string CreatePasswordHash(string password, string saltKey, string passwordFormat) => throw new NotSupportedException();
    }

    private sealed class NeverCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unsafe identifiers must be rejected before HTTP dispatch.");
    }
}
