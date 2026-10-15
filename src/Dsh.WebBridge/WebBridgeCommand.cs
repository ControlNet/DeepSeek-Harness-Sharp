using System.Globalization;
using System.Net.Sockets;
using Dsh.Transport;

namespace Dsh.WebBridge;

/** `dsharp host web [--port N] [--print-token]`: runs the bridge until cancelled, or prints the running bridge's access token. */
public static class WebBridgeCommand
{
    public const string PortOption = "--port";
    public const string PrintTokenOption = "--print-token";

    private const string Usage = "usage: dsharp host web [--port N] [--print-token]";

    private readonly record struct WebBridgeArguments(int Port, bool PrintToken, string? Error);

    /** ensureHostAsync runs before the bridge starts listening, e.g. to start the host daemon the bridge relays to. */
    public static async Task<int> RunAsync(
        string root,
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        Func<Task> ensureHostAsync,
        CancellationToken cancellationToken)
    {
        var (port, printToken, problem) = Parse(arguments);
        if (problem is not null)
        {
            await error.WriteLineAsync($"dsharp: {problem} ({Usage})").ConfigureAwait(false);
            return 2;
        }

        if (printToken)
            return await PrintTokenAsync(root, output, error, cancellationToken).ConfigureAwait(false);
        await ensureHostAsync().ConfigureAwait(false);
        return await ServeAsync(root, port, output, error, cancellationToken).ConfigureAwait(false);
    }

    private static WebBridgeArguments Parse(IReadOnlyList<string> arguments)
    {
        var port = WebBridgeEndpoint.DefaultPort;
        var printToken = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            switch (arguments[index])
            {
                case PrintTokenOption:
                    printToken = true;
                    break;
                case PortOption when index + 1 < arguments.Count
                    && ushort.TryParse(arguments[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed):
                    port = parsed;
                    index++;
                    break;
                default:
                    return new WebBridgeArguments(port, printToken, $"unexpected or invalid argument \"{arguments[index]}\"");
            }
        }

        return new WebBridgeArguments(port, printToken, null);
    }

    /** Prints the token alone on the first line (for scripts), then the one-time login URL that carries it in the fragment. */
    private static async Task<int> PrintTokenAsync(string root, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var portFile = WebBridgeEndpoint.PortFile(root);
        var token = LocalEndpointToken.Read(portFile);
        var portText = File.Exists(portFile) ? await File.ReadAllTextAsync(portFile, cancellationToken).ConfigureAwait(false) : "";
        if (token is null || !int.TryParse(portText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            await error.WriteLineAsync($"dsharp: no web bridge is running for {root}; start one with `dsharp host web`").ConfigureAwait(false);
            return 1;
        }

        await output.WriteLineAsync(token).ConfigureAwait(false);
        await output.WriteLineAsync($"{WebBridgeEndpoint.LocalUrl(port)}#token={token}").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> ServeAsync(string root, int port, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        WebBridgeServer server;
        try
        {
            server = WebBridgeServer.Start(new WebBridgeOptions(root, port));
        }
        catch (SocketException failure) when (failure.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            await error.WriteLineAsync($"dsharp: port {port} on 127.0.0.1 is already in use; pick another with {PortOption} N").ConfigureAwait(false);
            return 1;
        }

        await using (server)
        {
            await PrintBannerAsync(output, root, server.Port).ConfigureAwait(false);
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        return 0;
    }

    private static async Task PrintBannerAsync(TextWriter output, string root, int port)
    {
        var url = WebBridgeEndpoint.LocalUrl(port);
        await output.WriteLineAsync($"dsharp web bridge listening on {url} (loopback only)").ConfigureAwait(false);
        await output.WriteLineAsync($"web access token: {WebBridgeEndpoint.TokenFile(root)} (new on every start; show it with `dsharp host web {PrintTokenOption}`)").ConfigureAwait(false);
        await output.WriteLineAsync($"first login: open {url}#token=<token>, or paste the token into the login page").ConfigureAwait(false);
        await output.WriteLineAsync($"from another device: ssh -N -L {port}:127.0.0.1:{port} {Environment.UserName}@{Environment.MachineName}, then open {url} there").ConfigureAwait(false);
    }
}
