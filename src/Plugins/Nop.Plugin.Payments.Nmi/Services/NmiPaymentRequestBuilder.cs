using System.Net;
using Nop.Core;
using Nop.Core.Domain.Common;
using Nop.Core.Domain.Customers;
using Nop.Core.Domain.Directory;
using Nop.Plugin.Payments.Nmi.Domain;
using Nop.Plugin.Payments.Nmi.Services.Api;
using Nop.Services.Customers;
using Nop.Services.Directory;
using Nop.Services.Payments;
using Nop.Services.Stores;

namespace Nop.Plugin.Payments.Nmi.Services;

/// <summary>
/// Builds a Payment API sale/auth request from trusted server-side checkout data.
/// </summary>
public class NmiPaymentRequestBuilder
{
    private readonly ICountryService _countryService;
    private readonly ICurrencyService _currencyService;
    private readonly ICustomerService _customerService;
    private readonly IStateProvinceService _stateProvinceService;
    private readonly IStoreService _storeService;
    private readonly CurrencySettings _currencySettings;

    public NmiPaymentRequestBuilder(ICountryService countryService,
        ICurrencyService currencyService,
        ICustomerService customerService,
        IStateProvinceService stateProvinceService,
        IStoreService storeService,
        CurrencySettings currencySettings)
    {
        _countryService = countryService;
        _currencyService = currencyService;
        _customerService = customerService;
        _stateProvinceService = stateProvinceService;
        _storeService = storeService;
        _currencySettings = currencySettings;
    }

    public async Task<NmiPaymentRequest> BuildAsync(ProcessPaymentRequest paymentRequest,
        NmiSettings settings,
        Customer customer,
        string paymentToken)
    {
        ArgumentNullException.ThrowIfNull(paymentRequest);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(customer);

        var roundedAmount = decimal.Round(paymentRequest.OrderTotal, 2, MidpointRounding.AwayFromZero);
        if (roundedAmount <= 0 || roundedAmount != paymentRequest.OrderTotal)
            throw new NopException("NMI requires a positive order total with no more than two decimal places.");

        var currency = await _currencyService.GetCurrencyByIdAsync(_currencySettings.PrimaryStoreCurrencyId);
        var currencyCode = currency?.CurrencyCode?.Trim().ToUpperInvariant();
        if (currencyCode?.Length != 3)
            throw new NopException("The primary store currency must have a three-letter ISO code.");

        var billingAddress = await _customerService.GetCustomerBillingAddressAsync(customer);
        var shippingAddress = await _customerService.GetCustomerShippingAddressAsync(customer);
        var store = await _storeService.GetStoreByIdAsync(paymentRequest.StoreId);
        var orderId = paymentRequest.OrderGuid.ToString().Trim();

        return new NmiPaymentRequest
        {
            TransactionType = settings.TransactionMode == NmiTransactionMode.Authorize
                ? NmiTransactionTypes.Authorize
                : NmiTransactionTypes.Sale,
            Amount = roundedAmount,
            Currency = currencyCode,
            PaymentToken = paymentToken,
            ProcessorId = EmptyToNull(TrimTo(settings.ProcessorId, 50)),
            // Only override the processor's duplicate window when explicitly configured; some processors reject any override.
            DuplicateCheckSeconds = settings.DuplicateCheckSeconds > 0
                ? Math.Min(settings.DuplicateCheckSeconds, 7_862_400)
                : null,
            BillingAddress = await BuildAddressAsync(billingAddress),
            ShippingAddress = await BuildAddressAsync(shippingAddress),
            OrderId = orderId,
            OrderDescription = TrimTo($"{store?.Name ?? "nopCommerce"} order {orderId}", 100),
            IpAddress = IPAddress.TryParse(customer.LastIpAddress, out var ipAddress)
                ? ipAddress.ToString()
                : null
        };
    }

    private async Task<NmiAddress> BuildAddressAsync(Address address)
    {
        if (address is null)
            return null;

        var country = await _countryService.GetCountryByAddressAsync(address);
        var state = await _stateProvinceService.GetStateProvinceByAddressAsync(address);
        return new NmiAddress
        {
            FirstName = TrimTo(address.FirstName, 50),
            LastName = TrimTo(address.LastName, 50),
            Company = EmptyToNull(TrimTo(address.Company, 100)),
            Address1 = TrimTo(address.Address1, 100),
            Address2 = EmptyToNull(TrimTo(address.Address2, 100)),
            City = TrimTo(address.City, 50),
            State = EmptyToNull(TrimTo(state?.Abbreviation ?? state?.Name, 50)),
            Zip = TrimTo(address.ZipPostalCode, 20),
            Country = EmptyToNull(TrimTo(country?.TwoLetterIsoCode?.ToUpperInvariant(), 2)),
            Email = EmptyToNull(address.Email?.Trim()),
            Phone = EmptyToNull(address.PhoneNumber?.Trim()),
            Fax = EmptyToNull(TrimTo(address.FaxNumber, 50))
        };
    }

    private static string TrimTo(string value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, maxLength)];

    private static string EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
