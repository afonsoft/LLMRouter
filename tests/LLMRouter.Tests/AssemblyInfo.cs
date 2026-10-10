using System.Runtime.CompilerServices;
using Xunit;

// The suite shares process-wide statics (CooldownTracker threshold/cooldowns,
// ProviderBreaker state, model lockouts) across endpoint + unit tests.
// Serial execution avoids cross-test leakage; suite runs in ~8s anyway.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

// SPEC-074: tests need usage writes visible immediately after a request —
// force the UsageWriter into synchronous mode before any host is built.
internal static class TestInit
{
    [ModuleInitializer]
    internal static void Init() =>
        Environment.SetEnvironmentVariable("LLMR_SYNC_WRITES", "1");
}
