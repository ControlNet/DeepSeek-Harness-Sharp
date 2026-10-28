using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Dsh.Transport;

namespace Dsh.Tests;

/** Terminates real TLS for a localhost-only proxy certificate and delegates all proxy bytes to TestForwardProxy. */
internal sealed class HttpsForwardProxyFixture : IAsyncDisposable
{
    private const int MaximumConnections = 16;
    private const int SerialNumberLength = 16;
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    private static readonly TimeSpan CertificateClockSkew = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CertificateLifetime = TimeSpan.FromHours(1);
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<TcpClient, TcpClient> _clients = new();
    private readonly List<Task> _serving = [];
    private readonly object _disposeGate = new();
    private readonly X509Certificate2 _certificate;
    private readonly int _backendPort;
    private Task _accepting = Task.CompletedTask;
    private Task? _disposing;

    private HttpsForwardProxyFixture(string certificateFile, int backendPort, X509Certificate2 certificate)
    {
        CertificateFile = certificateFile;
        _backendPort = backendPort;
        _certificate = certificate;
    }

    public int Port { get; private set; }

    public string CertificateFile { get; }

    public static async Task<HttpsForwardProxyFixture> StartAsync(string root, TestForwardProxy backend, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var backendPort = backend.Port;
        var certificateFile = Path.Combine(root, $"https-proxy-ca-{Guid.NewGuid():N}.pem");
        var (rootPem, certificate) = CreateCertificates();
        var fixture = new HttpsForwardProxyFixture(certificateFile, backendPort, certificate);
        try
        {
            token.ThrowIfCancellationRequested();
            OwnerOnlyFiles.Write(fixture.CertificateFile, TrustBundle(rootPem));
            token.ThrowIfCancellationRequested();
            fixture._listener.Start(MaximumConnections);
            fixture.Port = ((IPEndPoint)fixture._listener.LocalEndpoint).Port;
            fixture._accepting = fixture.AcceptAsync();
            token.ThrowIfCancellationRequested();
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    private static string TrustBundle(string fixtureRoot)
    {
        using var roots = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        roots.Open(OpenFlags.ReadOnly);
        var certificates = roots.Certificates;
        try
        {
            return $"{fixtureRoot}{Environment.NewLine}{certificates.ExportCertificatePems()}";
        }
        finally
        {
            foreach (var certificate in certificates)
                certificate.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
            return new ValueTask(_disposing ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _accepting;
        }
        finally
        {
            foreach (var (client, backend) in _clients)
            {
                client.Dispose();
                backend.Dispose();
            }
            try
            {
                await Task.WhenAll(_serving);
            }
            finally
            {
                _certificate.Dispose();
                _stop.Dispose();
                File.Delete(CertificateFile);
            }
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (_serving.Count == MaximumConnections)
                    await Task.WhenAny(_serving).WaitAsync(_stop.Token);
                for (var index = _serving.Count - 1; index >= 0; index--)
                {
                    if (!_serving[index].IsCompleted)
                        continue;
                    await _serving[index];
                    _serving.RemoveAt(index);
                }
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                var backend = new TcpClient(AddressFamily.InterNetwork);
                _clients.TryAdd(client, backend);
                _serving.Add(ServeAsync(client, backend));
            }
        }
        catch (Exception error) when (_stop.IsCancellationRequested && error is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client, TcpClient backend)
    {
        try
        {
            using (client)
            using (backend)
            using (var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false))
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    ClientCertificateRequired = false,
                }, _stop.Token);
                await backend.ConnectAsync(IPAddress.Loopback, _backendPort, _stop.Token);
                using var upstream = backend.GetStream();
                using var relay = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                var send = tls.CopyToAsync(upstream, relay.Token);
                var receive = upstream.CopyToAsync(tls, relay.Token);
                try
                {
                    await Task.WhenAny(send, receive);
                }
                finally
                {
                    await relay.CancelAsync();
                    client.Dispose();
                    backend.Dispose();
                    await Task.WhenAll(send, receive);
                }
            }
        }
        catch (Exception error) when (error is IOException or SocketException or AuthenticationException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            _clients.TryRemove(client, out _);
        }
    }

    private static (string RootPem, X509Certificate2 Server) CreateCertificates()
    {
        var now = DateTimeOffset.UtcNow;
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest("CN=HTTPS forward proxy test root", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var root = rootRequest.CreateSelfSigned(now - CertificateClockSkew, now + CertificateLifetime);
        using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var serverRequest = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256);
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(ServerAuthenticationOid) }, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        serverRequest.CertificateExtensions.Add(names.Build());
        using var certificate = serverRequest.Create(root, now - CertificateClockSkew, now + CertificateLifetime,
            RandomNumberGenerator.GetBytes(SerialNumberLength));
        return (root.ExportCertificatePem(), certificate.CopyWithPrivateKey(serverKey));
    }
}
