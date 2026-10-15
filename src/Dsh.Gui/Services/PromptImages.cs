using System.Text.RegularExpressions;
using Dsh.Llm;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Gui.Services;

/**
 * Images a prompt refers to: image paths written in the prompt are attached and removed from the text, relative paths resolve against the session's directory.
 * Image paths inside text files the prompt mentions are attached too (one level deep), resolved against that file's directory first.
 * For a session on another machine every file is read from that machine.
 */
public static partial class PromptImages
{
    /** `@<relative or absolute path>` or a standalone absolute path ending in an image extension; `(?<!\S)` keeps `/bar.png` inside `@foo/bar.png` from matching on its own. */
    [GeneratedRegex("""(?<!\S)(?:@(?<m>[^\s@"']*?\.(?:png|jpe?g|gif|webp|avif))|(?<p>(?:[A-Za-z]:[\\/]|/)[^\s@"']*?\.(?:png|jpe?g|gif|webp|avif)))""", RegexOptions.IgnoreCase)]
    public static partial Regex PathPattern();

    public static async Task<(string Text, IReadOnlyList<RemoteImageBlock> Images)> ExtractRemoteAsync(
        IRemoteHost host, string cwd, RemotePathStyle style, string text, CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var images = new List<RemoteImageBlock>();
        var remaining = text;
        foreach (Match match in PathPattern().Matches(text))
        {
            var token = match.Groups["m"].Success ? match.Groups["m"].Value : match.Groups["p"].Value;
            if (token.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                continue;
            if (await TryAddAsync(host, [RemotePaths.Resolve(cwd, token, style)], style, seen, images, cancellationToken))
                remaining = remaining.Replace(match.Value, " ", StringComparison.Ordinal);
        }
        foreach (var reference in TextFileImageReferences.ReferenceTokens(text))
            await AddReferencedAsync(host, cwd, style, RemotePaths.Resolve(cwd, reference, style), seen, images, cancellationToken);
        return (remaining.Trim(), images);
    }

    private static async Task AddReferencedAsync(
        IRemoteHost host, string cwd, RemotePathStyle style, string file, HashSet<string> seen, List<RemoteImageBlock> images, CancellationToken cancellationToken)
    {
        if (ImageAttachments.MediaTypeForExtension(RemotePaths.Extension(file, style)) is not null)
            return;
        if (await ReadTextAsync(host, file, cancellationToken) is not { } content)
            return;
        var directory = RemotePaths.DirectoryName(file, style);
        foreach (var token in TextFileImageReferences.ImageTokens(content))
        {
            if (images.Count >= TextFileImageReferences.MaxImages)
                break;
            await TryAddAsync(host, [RemotePaths.Resolve(directory, token, style), RemotePaths.Resolve(cwd, token, style)], style, seen, images, cancellationToken);
        }
    }

    /** UTF-8 content of a remote text file; null when it is too large, contains NUL or cannot be read. */
    private static async Task<string?> ReadTextAsync(IRemoteHost host, string path, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await host.ReadFileAsync(path, cancellationToken);
            if (bytes.Length == 0 || bytes.Length > TextFileImageReferences.MaxTextBytes)
                return null;
            var content = System.Text.Encoding.UTF8.GetString(bytes);
            return content.Contains('\0') ? null : content;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or JsonRpcException)
        {
            return null;
        }
    }

    /** Tries the candidate paths in order; the first readable image is attached (media type by extension). */
    private static async Task<bool> TryAddAsync(
        IRemoteHost host, IReadOnlyList<string> candidates, RemotePathStyle style, HashSet<string> seen, List<RemoteImageBlock> images, CancellationToken cancellationToken)
    {
        if (images.Count >= TextFileImageReferences.MaxImages)
            return false;
        foreach (var candidate in candidates)
        {
            if (ImageAttachments.MediaTypeForExtension(RemotePaths.Extension(candidate, style)) is not { } mediaType || !seen.Add(candidate))
                continue;
            try
            {
                var bytes = await host.ReadFileAsync(candidate, cancellationToken);
                images.Add(new RemoteImageBlock(mediaType, 0, 0, RemotePaths.FileName(candidate, style), Convert.ToBase64String(bytes)));
                return true;
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or JsonRpcException)
            {
                seen.Remove(candidate);
            }
        }
        return false;
    }
}
