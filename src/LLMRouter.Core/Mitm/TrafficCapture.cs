using System.Collections.Concurrent;

namespace LLMRouter.Core.Mitm;

/// <summary>
/// SPEC-014: in-memory ring buffer of flows captured by the forward-proxy
/// middleware (and the inspector toggle).
/// </summary>
public static class TrafficCapture
{
    public sealed record Flow(
        string Id, DateTime At, string Method, string Host, string Path,
        int Status, int Ms, int ReqBytes, int RespBytes,
        string ReqHeaders, string? ReqBody, string RespHeaders, string? RespBody);

    private static readonly ConcurrentQueue<Flow> Flows = new();
    private const int Cap = 500;

    public static bool Enabled { get; set; }

    public static void Add(Flow f)
    {
        Flows.Enqueue(f);
        while (Flows.Count > Cap && Flows.TryDequeue(out _)) { }
    }

    public static IReadOnlyList<Flow> List() => Flows.Reverse().ToList();

    public static Flow? Find(string id) => Flows.FirstOrDefault(f => f.Id == id);

    public static void Clear() { while (Flows.TryDequeue(out _)) { } }
}
