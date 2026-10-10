namespace LLMRouter.Server;

public static class FrameworkAssets
{
    /// <summary>
    /// Resolve an unfingerprinted request name (e.g. "dotnet.js", "blazor.webassembly")
    /// to the fingerprinted file on disk. Never pick sourcemaps or compressed variants —
    /// "dotnet.js.*" would otherwise match "dotnet.js.map" and serve it as JS.
    /// </summary>
    public static string? Resolve(string dir, string name)
    {
        var ext = Path.GetExtension(name);
        // try "name.*" first (e.g. blazor.webassembly → blazor.webassembly.<fp>.js),
        // then "stem.*ext" (e.g. foo.js → foo.<fp>.js)
        return new[] { name + ".*", ext.Length > 0 ? name[..^ext.Length] + ".*" + ext : null }
            .Where(p => p != null)
            .SelectMany(p => Directory.EnumerateFiles(dir, p!))
            .Where(f => !f.EndsWith(".br") && !f.EndsWith(".gz") && !f.EndsWith(".map"))
            .OrderBy(f => f.Length).FirstOrDefault();
    }
}
