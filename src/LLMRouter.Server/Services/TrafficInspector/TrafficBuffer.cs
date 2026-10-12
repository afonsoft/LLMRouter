using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLMRouter.Server.Services.TrafficInspector;

/// <summary>
/// In-memory ring buffer for intercepted traffic — port of TrafficBuffer
/// (mitm/inspector/buffer.ts). Capacity: LLMR_INSPECTOR_BUFFER_SIZE (default
/// 1000) entries, bodies clamped to LLMR_INSPECTOR_MAX_BODY_KB (default
/// 1024 KiB) with the upstream truncation marker. Push applies detectKind +
/// contextKey; mutations broadcast to WebSocket subscribers.
/// </summary>
public sealed class TrafficBuffer
{
    private const string TruncationMarker = "\n…(truncated for performance)";
    private readonly int _maxSize;
    private readonly int _maxBodyBytes;
    private readonly LinkedList<InterceptedRequest> _buffer = new();
    private readonly HashSet<Action<JsonObject>> _subscribers = new();
    private readonly object _gate = new();

    public TrafficBuffer()
    {
        _maxSize = EnvInt("LLMR_INSPECTOR_BUFFER_SIZE", 1000);
        _maxBodyBytes = EnvInt("LLMR_INSPECTOR_MAX_BODY_KB", 1024) * 1024;
    }

    private static int EnvInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

    /// <summary>Append an entry: fills detectedKind/contextKey, clamps bodies, broadcasts "new".</summary>
    public void Push(InterceptedRequest req)
    {
        req.DetectedKind ??= InspectorModel.DetectKind(req);
        if (req.ContextKey is null && req.DetectedKind == "llm")
            req.ContextKey = InspectorModel.ComputeContextKey(req);
        req.RequestBody = CapBody(req.RequestBody);
        req.ResponseBody = CapBody(req.ResponseBody);
        lock (_gate)
        {
            _buffer.AddLast(req);
            while (_buffer.Count > _maxSize) _buffer.RemoveFirst();
            Broadcast("new", req);
        }
    }

    /// <summary>Update an entry in place by id — no-op when already rotated out.</summary>
    public void Update(string id, InterceptedRequest req)
    {
        req.RequestBody = CapBody(req.RequestBody);
        req.ResponseBody = CapBody(req.ResponseBody);
        lock (_gate)
        {
            for (var n = _buffer.First; n is not null; n = n.Next)
            {
                if (n.Value.Id != id) continue;
                n.Value = req;
                Broadcast("update", req);
                return;
            }
        }
    }

    public int Count { get { lock (_gate) return _buffer.Count; } }

    public InterceptedRequest? Get(string id)
    {
        lock (_gate)
            return _buffer.FirstOrDefault(r => r.Id == id);
    }

    /// <summary>Filtered snapshot (linear scan — buffer is bounded to ~1000).</summary>
    public List<InterceptedRequest> List(ListFilters? filters = null)
    {
        lock (_gate)
            return _buffer.Where(r => InspectorModel.Matches(r, filters)).ToList();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _buffer.Clear();
            Broadcast("clear", null);
        }
    }

    /// <summary>Subscribe to mutation events; returns an unsubscribe action.</summary>
    public Action Subscribe(Action<JsonObject> onEvent)
    {
        lock (_gate) _subscribers.Add(onEvent);
        return () => { lock (_gate) _subscribers.Remove(onEvent); };
    }

    private void Broadcast(string type, InterceptedRequest? data)
    {
        if (_subscribers.Count == 0) return;
        var ev = new JsonObject { ["type"] = type };
        if (data is not null)
            ev["data"] = JsonSerializer.SerializeToNode(data);
        var msg = ev;
        foreach (var sub in _subscribers)
        {
            try { sub(msg); }
            catch (Exception) { /* a dead ws subscriber is pruned by its own loop */ }
        }
    }

    private string? CapBody(string? body)
        => body is not null && body.Length > _maxBodyBytes
            ? body[.._maxBodyBytes] + TruncationMarker : body;
}
