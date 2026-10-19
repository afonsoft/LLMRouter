using System.Threading.Channels;
using LLMRouter.Core.Data;

namespace LLMRouter.Core.Gateway;

/// <summary>SPEC-148: broadcast de RequestDetails recém-inseridos p/ o SSE tail.
/// Bounded+DropOldest — nunca bloqueia o hot path de escrita.</summary>
public static class RequestLogTail
{
    private static readonly Channel<RequestDetail> Ch = Channel.CreateBounded<RequestDetail>(
        new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest });

    public static ChannelReader<RequestDetail> Stream => Ch.Reader;

    public static void Publish(RequestDetail r) => Ch.Writer.TryWrite(r);
}
