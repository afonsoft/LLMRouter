namespace LLMRouter.Core.Gateway;

/// <summary>
/// SPEC-066: global upstream concurrency cap — a shared semaphore applied by
/// <see cref="ConcurrencyGateHandler"/> to every send on the "upstream" client.
/// <see cref="Limit"/> 0 = unlimited (semaphore bypassed). Persisted in
/// settings.data.concurrency; updated live via PUT /api/admin/concurrency.
/// </summary>
public static class ConcurrencyGate
{
    private static readonly object Gate = new();
    private static SemaphoreSlim? _sem;
    private static int _limit;
    private static int _inFlight;

    /// <summary>Current cap (0 = unlimited).</summary>
    public static int Limit => _limit;

    /// <summary>Number of upstream requests currently in flight.</summary>
    public static int InFlight => _inFlight;

    /// <summary>Reconfigure the cap at runtime; in-flight slots are preserved.</summary>
    public static void Set(int limit)
    {
        lock (Gate)
        {
            _limit = Math.Max(0, limit);
            _sem = _limit > 0 ? new SemaphoreSlim(_limit) : null;
        }
    }

    /// <summary>Acquire a slot (no-op when unlimited). Caller must <see cref="Release"/>.</summary>
    public static async Task<bool> AcquireAsync(CancellationToken ct)
    {
        var s = _sem;
        if (s is null) { Interlocked.Increment(ref _inFlight); return true; }
        await s.WaitAsync(ct);
        Interlocked.Increment(ref _inFlight);
        return true;
    }

    /// <summary>Release a previously acquired slot.</summary>
    public static void Release()
    {
        Interlocked.Decrement(ref _inFlight);
        _sem?.Release();
    }
}

/// <summary>DelegatingHandler applying <see cref="ConcurrencyGate"/> to upstream sends.</summary>
public sealed class ConcurrencyGateHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await ConcurrencyGate.AcquireAsync(cancellationToken);
        try { return await base.SendAsync(request, cancellationToken); }
        finally { ConcurrencyGate.Release(); }
    }
}
