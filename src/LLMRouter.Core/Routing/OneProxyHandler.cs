namespace LLMRouter.Core.Routing;

/// <summary>SPEC-047: holds the current oneproxy url; the gateway refreshes
/// it per dispatch from settings.oneproxy (+ rotate-on-fail).</summary>
public sealed class OneProxyState
{
    private volatile string? _current;
    public string? Current => _current;
    public void Set(string? url) => _current = url;
}

/// <summary>Primary handler for the "upstream" named client: builds a real
/// SocketsHttpHandler per proxy url and swaps it when the url changes.</summary>
public sealed class OneProxyHandler(OneProxyState state) : HttpMessageHandler
{
    private readonly object _gate = new();
    private HttpMessageInvoker _inner = new(new SocketsHttpHandler());
    private string? _builtFor;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = state.Current;
        if (url != _builtFor)
            lock (_gate)
            {
                if (url != _builtFor)
                {
                    var old = _inner;
                    _inner = new HttpMessageInvoker(url is null
                        ? new SocketsHttpHandler()
                        : new SocketsHttpHandler
                        { Proxy = new System.Net.WebProxy(url), UseProxy = true });
                    _builtFor = url;
                    try { old.Dispose(); } catch { }
                }
            }
        return _inner.SendAsync(request, cancellationToken);
    }
}
