namespace Vessel.Domain.Proxy;

/// <summary>Operator intent for the proxy container (upstream <c>force_stop</c> inverted).</summary>
public enum ProxyDesiredState
{
    Running = 0,

    /// <summary>An operator stopped the proxy; deployments must not start it again.</summary>
    Stopped = 1
}
