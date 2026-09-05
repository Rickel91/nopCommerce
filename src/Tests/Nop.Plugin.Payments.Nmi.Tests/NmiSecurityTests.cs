using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Http;
using Nop.Core.Domain.Common;
using Nop.Core.Domain.Customers;
using Nop.Core.Domain.Directory;
using Nop.Core.Domain.Stores;
using Nop.Plugin.Payments.Nmi.Services;
using Nop.Plugin.Payments.Nmi.Services.Api;
using Nop.Services.Customers;
using Nop.Services.Directory;
using Nop.Services.Localization;
using Nop.Services.Logging;
using Nop.Services.Payments;
using Nop.Services.Security;
using Nop.Services.Stores;
using Xunit;

namespace Nop.Plugin.Payments.Nmi.Tests;

public class NmiSecurityTests
{
    private static readonly string[] SensitiveSentinels =
    {
        "private-key-sentinel",
        "payment-token-sentinel",
        "signing-key-sentinel",
        "4111111111111111",
        "987"
    };

    [Fact]
    public void PluginRuntimeSurfacesExposeNoPanOrCvvMembers()
    {
        string[] forbiddenNames =
        {
            "pan", "primaryaccountnumber", "cardnumber", "card_number", "ccnumber", "cardcode", "card_code",
            "cvv", "cvv2", "cvc", "cvc2", "ccexp"
        };
        var assembly = typeof(NmiHttpClient).Assembly;
        var exposedMembers = assembly.GetTypes()
            .Where(type => type.Namespace?.StartsWith("Nop.Plugin.Payments.Nmi", StringComparison.Ordinal) == true)
            .SelectMany(type => type.GetMembers(BindingFlags.Instance | BindingFlags.Public |
                                                 BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(member => member.MemberType is MemberTypes.Field or MemberTypes.Property)
                .Select(member => $"{type.FullName}.{member.Name}"))
            .Where(member => forbiddenNames.Any(name =>
                member.EndsWith($".{name}", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.Empty(exposedMembers);
    }

    [Fact]
    public void PaymentFixturesContainNoCredentialOrCardDataFields()
    {
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var forbiddenMarkers = new[]
        {
            "security_key", "private_api_key", "webhook_signing_key", "payment_token",
            "ccnumber", "card_number", "cardnumber", "card_code", "cardcode", "cvv", "cvc"
        };

        foreach (var fixture in Directory.GetFiles(fixtureDirectory, "*.json"))
        {
            var content = File.ReadAllText(fixture);
            Assert.All(forbiddenMarkers, marker =>
                Assert.DoesNotContain(marker, content, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void PaymentDiagnosticMappingRemovesEverySensitiveCategory()
    {
        var response = new NmiTransactionResponse
        {
            Response = "security_key=private-key-sentinel",
            ResponseCode = "payment_token=payment-token-sentinel",
            ResponseText = "webhook_signing_key=signing-key-sentinel ccnumber=4111111111111111 cvv=987"
        };

        AssertContainsNoSensitiveValues(NmiPaymentResultMapper.BuildDiagnostic(response));
    }

    [Fact]
    public void FollowOnDiagnosticMappingRemovesCardAndSecretData()
    {
        var response = new NmiTransactionResponse
        {
            TransactionId = "refund-txn",
            Response = "1",
            ResponseCode = "security_key=private-key-sentinel",
            ResponseText = "payment_token=payment-token-sentinel card_number=4111111111111111 cvv=987 webhook_signing_key=signing-key-sentinel"
        };

        var outcome = NmiOrderOperationMapper.Map(new(response, null, 200), "refund", "root-payment");

        Assert.True(outcome.Success);
        AssertContainsNoSensitiveValues(outcome.Diagnostic);
    }

    [Fact]
    public async Task InitialPaymentLoggerReceivesNoKeyTokenSigningPanOrCvvValues()
    {
        var messages = new List<string>();
        var logger = TestInterfaceProxy.Create<ILogger>((method, args) =>
        {
            if (method.Name is nameof(ILogger.InformationAsync) or nameof(ILogger.WarningAsync))
                messages.Add((string)args[0]);
            return Task.CompletedTask;
        });
        var customer = new Customer { Id = 3, LastIpAddress = "192.0.2.10" };
        var customerService = TestInterfaceProxy.Create<ICustomerService>((method, _) => method.Name switch
        {
            nameof(ICustomerService.GetCustomerByIdAsync) => Task.FromResult(customer),
            nameof(ICustomerService.GetCustomerBillingAddressAsync) => Task.FromResult<Address>(null),
            nameof(ICustomerService.GetCustomerShippingAddressAsync) => Task.FromResult<Address>(null),
            _ => throw new NotSupportedException(method.Name)
        });
        var localization = TestInterfaceProxy.Create<ILocalizationService>((method, _) =>
            method.Name == nameof(ILocalizationService.GetResourceAsync)
                ? Task.FromResult("localized-message")
                : throw new NotSupportedException(method.Name));
        var handler = new LogSafetyHandler();
        var processor = new NmiPaymentProcessor(customerService, new HttpContextAccessor(), localization, new NmiLogger(logger),
            new NmiHttpClient(new HttpClient(handler), new PassThroughEncryptionService()),
            new NmiPaymentExecutionGuard(), RequestBuilder(customerService));
        var request = new ProcessPaymentRequest
        {
            CustomerId = customer.Id,
            StoreId = 7,
            OrderGuid = Guid.NewGuid(),
            OrderTotal = 10.25m
        };
        request.CustomValues[NmiDefaults.PaymentTokenCustomValueKey] = "payment-token-sentinel";
        var settings = new NmiSettings
        {
            EncryptedPrivateApiKey = "private-key-sentinel",
            EnableDebugLogging = true
        };

        var result = await processor.ProcessAsync(settings, request);

        Assert.True(result.Success);
        Assert.Contains("payment_token=payment-token-sentinel", handler.Body);
        Assert.Empty(request.CustomValues);
        // Debug logging emits a "sending" entry and an "approved" entry; neither may carry secrets or card data.
        Assert.Equal(2, messages.Count);
        Assert.All(messages, message => Assert.StartsWith("NMI: ", message));
        Assert.All(messages, AssertContainsNoSensitiveValues);
    }

    private static void AssertContainsNoSensitiveValues(string value)
    {
        Assert.All(SensitiveSentinels, sentinel =>
            Assert.DoesNotContain(sentinel, value, StringComparison.Ordinal));
    }

    private static NmiPaymentRequestBuilder RequestBuilder(ICustomerService customerService)
    {
        var countryService = TestInterfaceProxy.Create<ICountryService>((method, _) =>
            throw new NotSupportedException(method.Name));
        var currencyService = TestInterfaceProxy.Create<ICurrencyService>((method, _) =>
            method.Name == nameof(ICurrencyService.GetCurrencyByIdAsync)
                ? Task.FromResult(new Currency { CurrencyCode = "USD" })
                : throw new NotSupportedException(method.Name));
        var stateService = TestInterfaceProxy.Create<IStateProvinceService>((method, _) =>
            throw new NotSupportedException(method.Name));
        var storeService = TestInterfaceProxy.Create<IStoreService>((method, _) =>
            method.Name == nameof(IStoreService.GetStoreByIdAsync)
                ? Task.FromResult(new Store { Id = 7, Name = "Security Test Store" })
                : throw new NotSupportedException(method.Name));
        return new NmiPaymentRequestBuilder(countryService, currencyService, customerService,
            stateService, storeService, new CurrencySettings { PrimaryStoreCurrencyId = 1 });
    }

    private sealed class LogSafetyHandler : HttpMessageHandler
    {
        public string Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Body = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "response=1&responsetext=webhook_signing_key%3Dsigning-key-sentinel+ccnumber%3D4111111111111111+cvv%3D987" +
                    "&authcode=123456&transactionid=txn-safe&avsresponse=&cvvresponse=&orderid=&type=sale&response_code=100",
                    Encoding.UTF8, "text/html")
            };
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
