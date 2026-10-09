using Xunit;

// The suite shares process-wide statics (CooldownTracker threshold/cooldowns,
// ProviderBreaker state, model lockouts) across endpoint + unit tests.
// Serial execution avoids cross-test leakage; suite runs in ~8s anyway.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
