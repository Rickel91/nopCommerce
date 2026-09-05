using System.Collections.Concurrent;

namespace Nop.Plugin.Payments.Nmi.Services;

public sealed class NmiPaymentExecutionGuard
{
    private readonly ConcurrentDictionary<Guid, byte> _activeOrderCorrelations = new();

    public bool TryAcquire(Guid orderCorrelation, out IDisposable lease)
    {
        lease = null;
        if (orderCorrelation == Guid.Empty || !_activeOrderCorrelations.TryAdd(orderCorrelation, 0))
            return false;

        lease = new ExecutionLease(_activeOrderCorrelations, orderCorrelation);
        return true;
    }

    private sealed class ExecutionLease : IDisposable
    {
        private ConcurrentDictionary<Guid, byte> _activeOrderCorrelations;
        private readonly Guid _orderCorrelation;

        public ExecutionLease(ConcurrentDictionary<Guid, byte> activeOrderCorrelations, Guid orderCorrelation)
        {
            _activeOrderCorrelations = activeOrderCorrelations;
            _orderCorrelation = orderCorrelation;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _activeOrderCorrelations, null)?.TryRemove(_orderCorrelation, out _);
        }
    }
}
