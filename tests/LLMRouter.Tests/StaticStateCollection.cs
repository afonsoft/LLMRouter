namespace LLMRouter.Tests;

/// <summary>
/// Tests that mutate process-wide statics (CooldownTracker.Configure,
/// ProviderBreaker.HerdWindow) share this collection so xUnit never runs
/// them in parallel with each other.
/// </summary>
[CollectionDefinition("StaticState")]
public class StaticStateCollection { }
