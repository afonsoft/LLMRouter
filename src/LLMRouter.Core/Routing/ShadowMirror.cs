namespace LLMRouter.Core.Routing;

public static class ShadowMirror
{
    public static bool IsDivergent(int primaryStatus, long primaryTokens,
        int mirrorStatus, long mirrorTokens) =>
        primaryStatus != mirrorStatus || primaryTokens != mirrorTokens;
}
