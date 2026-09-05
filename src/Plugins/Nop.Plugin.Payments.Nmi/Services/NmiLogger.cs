using Nop.Core.Domain.Customers;
using Nop.Services.Logging;

namespace Nop.Plugin.Payments.Nmi.Services;

/// <summary>
/// Thin wrapper over the nopCommerce logger that redacts secrets, prefixes plugin entries, honours the
/// per-store debug switch, and never lets a logging failure change a payment outcome.
/// </summary>
public class NmiLogger
{
    private const string Prefix = "NMI: ";
    private readonly ILogger _logger;

    public NmiLogger(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// A logger that discards everything. Used where no host logger is available (unit tests).
    /// </summary>
    public static NmiLogger None { get; } = new(null);

    /// <summary>
    /// Writes an Information entry only when sanitized debug logging is enabled for the store.
    /// </summary>
    public Task DebugAsync(NmiSettings settings, string message, Customer customer = null) =>
        settings?.EnableDebugLogging == true
            ? WriteAsync(LogLevel.Information, message, null, customer)
            : Task.CompletedTask;

    public Task InformationAsync(string message, Customer customer = null) =>
        WriteAsync(LogLevel.Information, message, null, customer);

    public Task WarningAsync(string message, Exception exception = null, Customer customer = null) =>
        WriteAsync(LogLevel.Warning, message, exception, customer);

    /// <summary>
    /// Reserved for outcomes that need operator attention, such as ambiguous gateway results where funds may have moved.
    /// </summary>
    public Task ErrorAsync(string message, Exception exception = null, Customer customer = null) =>
        WriteAsync(LogLevel.Error, message, exception, customer);

    private async Task WriteAsync(LogLevel level, string message, Exception exception, Customer customer)
    {
        if (_logger is null || string.IsNullOrWhiteSpace(message))
            return;

        // Every entry starts with "NMI: " so operators can filter the system log on one prefix.
        var text = message.StartsWith(Prefix, StringComparison.Ordinal) ? message
            : message.StartsWith("NMI ", StringComparison.Ordinal) ? Prefix + message[4..]
            : Prefix + message;
        text = NmiRedactor.Redact(text);
        try
        {
            switch (level)
            {
                case LogLevel.Error:
                    await _logger.ErrorAsync(text, exception, customer);
                    break;
                case LogLevel.Warning:
                    await _logger.WarningAsync(text, exception, customer);
                    break;
                default:
                    await _logger.InformationAsync(text, exception, customer);
                    break;
            }
        }
        catch
        {
            // Diagnostics must never alter an already classified gateway result.
        }
    }

    private enum LogLevel
    {
        Information,
        Warning,
        Error
    }
}
