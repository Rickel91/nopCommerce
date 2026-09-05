using Nop.Core.Domain.Common;
using Nop.Core.Domain.Customers;
using Nop.Core.Domain.Directory;
using Nop.Core.Domain.Stores;
using Nop.Plugin.Payments.Nmi.Domain;
using Nop.Plugin.Payments.Nmi.Services;
using Nop.Plugin.Payments.Nmi.Services.Api;
using Nop.Services.Customers;
using Nop.Services.Directory;
using Nop.Services.Payments;
using Nop.Services.Stores;
using Xunit;

namespace Nop.Plugin.Payments.Nmi.Tests;

public class NmiPaymentRequestBuilderTests
{
    [Theory]
    [InlineData(NmiTransactionMode.AuthorizeAndCapture, NmiTransactionTypes.Sale)]
    [InlineData(NmiTransactionMode.Authorize, NmiTransactionTypes.Authorize)]
    public async Task TrustedCheckoutDataMapsDeterministically(NmiTransactionMode mode, string expectedType)
    {
        var orderGuid = Guid.Parse("2f0b5389-ae4a-4f12-9851-15a6f715ee26");
        var request = new ProcessPaymentRequest
        {
            OrderGuid = orderGuid,
            OrderTotal = 42.75m,
            StoreId = 7
        };
        var settings = new NmiSettings
        {
            TransactionMode = mode,
            ProcessorId = "  processor-one  ",
            DuplicateCheckSeconds = 90
        };
        var customer = new Customer { LastIpAddress = "192.0.2.44" };

        var actual = await Builder().BuildAsync(request, settings, customer, "single-use-token");

        Assert.Equal(expectedType, actual.TransactionType);
        Assert.Equal(42.75m, actual.Amount);
        Assert.Equal("USD", actual.Currency);
        Assert.Equal("single-use-token", actual.PaymentToken);
        Assert.Equal("processor-one", actual.ProcessorId);
        Assert.Equal(90, actual.DuplicateCheckSeconds);
        Assert.Equal(orderGuid.ToString("N"), actual.OrderId);
        Assert.Equal($"Store Seven order {orderGuid:N}", actual.OrderDescription);
        Assert.Equal("192.0.2.44", actual.IpAddress);
        Assert.Null(actual.BillingAddress);
        Assert.Null(actual.ShippingAddress);
    }

    [Fact]
    public void FormFieldsUseClassicPaymentApiVariableNamesAndOmitEmptyValues()
    {
        var request = new NmiPaymentRequest
        {
            TransactionType = NmiTransactionTypes.Sale,
            Amount = 12.3m,
            Currency = "USD",
            PaymentToken = "token-value",
            DuplicateCheckSeconds = 0,
            OrderId = "order-1",
            BillingAddress = new NmiAddress { FirstName = "Ada", LastName = "Lovelace", Zip = "12345", Country = "US" },
            ShippingAddress = new NmiAddress { FirstName = "Ada", City = "London" }
        };

        var fields = request.ToFormFields().ToDictionary(field => field.Key, field => field.Value);

        Assert.Equal("sale", fields["type"]);
        Assert.Equal("12.30", fields["amount"]);
        Assert.Equal("token-value", fields["payment_token"]);
        Assert.Equal("0", fields["dup_seconds"]);
        Assert.Equal("ecommerce", fields["industry"]);
        Assert.Equal("Ada", fields["first_name"]);
        Assert.Equal("Lovelace", fields["last_name"]);
        Assert.Equal("12345", fields["zip"]);
        Assert.Equal("US", fields["country"]);
        Assert.Equal("Ada", fields["shipping_firstname"]);
        Assert.Equal("London", fields["shipping_city"]);
        Assert.DoesNotContain("processor_id", fields.Keys);
        Assert.DoesNotContain("company", fields.Keys);
        Assert.DoesNotContain("shipping_lastname", fields.Keys);
        Assert.DoesNotContain("security_key", fields.Keys);
        Assert.All(fields.Values, value => Assert.False(string.IsNullOrEmpty(value)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.001)]
    public async Task InvalidTrustedOrderTotalsAreRejected(decimal amount)
    {
        var request = new ProcessPaymentRequest
        {
            OrderGuid = Guid.NewGuid(),
            OrderTotal = amount,
            StoreId = 7
        };

        await Assert.ThrowsAsync<Nop.Core.NopException>(() =>
            Builder().BuildAsync(request, new NmiSettings(), new Customer(), "single-use-token"));
    }

    private static NmiPaymentRequestBuilder Builder()
    {
        var countryService = TestInterfaceProxy.Create<ICountryService>((method, _) =>
            throw new NotSupportedException(method.Name));
        var currencyService = TestInterfaceProxy.Create<ICurrencyService>((method, _) =>
            method.Name == nameof(ICurrencyService.GetCurrencyByIdAsync)
                ? Task.FromResult(new Currency { CurrencyCode = "usd" })
                : throw new NotSupportedException(method.Name));
        var customerService = TestInterfaceProxy.Create<ICustomerService>((method, _) =>
            method.Name is nameof(ICustomerService.GetCustomerBillingAddressAsync) or
                nameof(ICustomerService.GetCustomerShippingAddressAsync)
                ? Task.FromResult<Address>(null)
                : throw new NotSupportedException(method.Name));
        var stateService = TestInterfaceProxy.Create<IStateProvinceService>((method, _) =>
            throw new NotSupportedException(method.Name));
        var storeService = TestInterfaceProxy.Create<IStoreService>((method, _) =>
            method.Name == nameof(IStoreService.GetStoreByIdAsync)
                ? Task.FromResult(new Store { Id = 7, Name = "Store Seven" })
                : throw new NotSupportedException(method.Name));

        return new NmiPaymentRequestBuilder(countryService, currencyService, customerService,
            stateService, storeService, new CurrencySettings { PrimaryStoreCurrencyId = 1 });
    }
}
