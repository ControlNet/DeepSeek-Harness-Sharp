using System.Reflection;

namespace Dsh.WebBridge;

/** One embedded front-end file ready to be served. */
internal sealed record StaticAsset(byte[] Content, string ContentType);

/**
 * The front end embedded in this assembly under wwwroot/ (see Dsh.WebBridge.csproj).
 * A request path is looked up as an exact key among the embedded names, so no path from the request ever reaches the file system.
 */
internal static class StaticAssets
{
    private const string ResourcePrefix = "wwwroot/";
    private const string IndexFile = "index.html";
    private const string OctetStream = "application/octet-stream";

    private static readonly Assembly Owner = typeof(StaticAssets).Assembly;

    /** Request path without the leading '/' mapped to the manifest resource name; MSBuild's RecursiveDir uses '\' on Windows. */
    private static readonly Dictionary<string, string> ResourceNames = Owner.GetManifestResourceNames()
        .Where(name => name.Replace('\\', '/').StartsWith(ResourcePrefix, StringComparison.Ordinal))
        .ToDictionary(name => name.Replace('\\', '/')[ResourcePrefix.Length..], name => name, StringComparer.Ordinal);

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".json"] = "application/json",
        [".webmanifest"] = "application/manifest+json",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".ico"] = "image/x-icon",
        [".woff2"] = "font/woff2",
    };

    /** Returns null when no embedded file has this path; "/" serves index.html. */
    public static async Task<StaticAsset?> LoadAsync(string path, CancellationToken cancellationToken)
    {
        var relative = path == "/" ? IndexFile : path[1..];
        if (!ResourceNames.TryGetValue(relative, out var resource))
            return null;
        await using var stream = Owner.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"embedded resource {resource} disappeared");
        using var content = new MemoryStream();
        await stream.CopyToAsync(content, cancellationToken).ConfigureAwait(false);
        return new StaticAsset(content.ToArray(), ContentTypes.GetValueOrDefault(Path.GetExtension(relative), OctetStream));
    }
}
