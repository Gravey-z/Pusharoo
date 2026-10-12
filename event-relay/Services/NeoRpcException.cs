namespace Pusharoo.EventRelay.Services;

public enum NeoRpcFailureKind { Transient, UnavailableHistory, InvalidResponse, NetworkMismatch }

public sealed class NeoRpcException(string endpoint, string method, NeoRpcFailureKind kind, string message, Exception? inner = null)
    : Exception($"Neo RPC {method} at {endpoint}: {message}", inner)
{
    public string Endpoint { get; } = endpoint;
    public string Method { get; } = method;
    public NeoRpcFailureKind Kind { get; } = kind;
}
