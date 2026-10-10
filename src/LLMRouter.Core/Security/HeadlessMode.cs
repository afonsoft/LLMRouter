namespace LLMRouter.Core.Security;

/// <summary>SPEC-086: headless gateway-only mode flag (set once in Program.cs).</summary>
public static class HeadlessMode
{
    /// <summary>True when the server runs with the dashboard disabled.</summary>
    public static bool Enabled { get; set; }
}
