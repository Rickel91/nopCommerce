using System.Reflection;

namespace Nop.Plugin.Payments.Nmi.Tests;

internal static class TestInterfaceProxy
{
    public static T Create<T>(Func<MethodInfo, object[], object> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, DelegateDispatchProxy>();
        ((DelegateDispatchProxy)(object)proxy).Handler = handler;
        return proxy;
    }
}

public class DelegateDispatchProxy : DispatchProxy
{
    internal Func<MethodInfo, object[], object> Handler { get; set; }

    protected override object Invoke(MethodInfo targetMethod, object[] args) =>
        Handler?.Invoke(targetMethod, args) ??
        throw new NotSupportedException($"Unexpected test call: {targetMethod?.DeclaringType?.Name}.{targetMethod?.Name}");
}
