#nullable enable
using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Chain;

/// <summary>
/// Where an Electrum server is. Accepts Electrum's "host:port:s" / "host:port:t" notation
/// and "ssl://host:port" / "tcp://host:port". A self-signed certificate can be trusted by
/// pinning its SHA-256 fingerprint: "ssl://host:port?fingerprint=AB:CD:...".
/// </summary>
public record ElectrumEndpoint(string Host, int Port, bool UseTls, byte[]? PinnedCertificateSha256)
{
    public static bool LooksLikeElectrum(string url) =>
        !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public static ElectrumEndpoint Parse(string url)
    {
        url = url.Trim();
        if (url.Contains("://", StringComparison.Ordinal))
        {
            var uri = new Uri(url);
            var tls = uri.Scheme.ToLowerInvariant() switch
            {
                "ssl" or "tls" => true,
                "tcp" => false,
                _ => throw new FormatException($"Unknown Electrum scheme '{uri.Scheme}' (use ssl:// or tcp://)")
            };
            if (uri.Port <= 0)
                throw new FormatException("The Electrum server needs a port, such as ssl://host:50002");
            byte[]? pin = null;
            foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 && kv[0].Equals("fingerprint", StringComparison.OrdinalIgnoreCase))
                    pin = Convert.FromHexString(Uri.UnescapeDataString(kv[1]).Replace(":", "", StringComparison.Ordinal));
            }
            if (pin is not null && (!tls || pin.Length != 32))
                throw new FormatException("A certificate fingerprint must be a SHA-256 fingerprint on an ssl:// server");
            return new ElectrumEndpoint(uri.Host, uri.Port, tls, pin);
        }
        var parts = url.Split(':');
        if (parts.Length == 3 && int.TryParse(parts[1], out var port) && parts[2] is "s" or "t")
            return new ElectrumEndpoint(parts[0], port, parts[2] == "s", null);
        throw new FormatException($"'{url}' is not an Electrum server address (expected host:port:s, ssl://host:port or tcp://host:port)");
    }

    public string Name => $"{Host}:{Port}";
    public override string ToString() => $"{(UseTls ? "ssl" : "tcp")}://{Host}:{Port}";
}

public class ElectrumException(string message) : ChainSourceException(message);

/// <summary>
/// A minimal Electrum protocol client: one persistent connection, one request at a time,
/// reconnecting when the connection drops. Subscription notifications are ignored.
/// </summary>
public sealed class ElectrumClient(ElectrumEndpoint endpoint) : IDisposable
{
    // BLAKE2b servers refuse clients below 1.8, which they assume can't read 164-byte headers.
    // This client never parses headers, so any version from 1.4 up works.
    static readonly string[] ProtocolVersions = ["1.4", "1.8"];
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    readonly SemaphoreSlim _lock = new(1, 1);
    TcpClient? _tcp;
    StreamReader? _reader;
    Stream? _stream;
    int _nextId;

    public ElectrumEndpoint Endpoint => endpoint;

    public async Task<JToken> CallAsync(string method, object[] parameters, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(Timeout);
                try
                {
                    await EnsureConnectedAsync(cts.Token);
                    return await SendAsync(method, parameters, cts.Token);
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException
                                           && !cancellationToken.IsCancellationRequested)
                {
                    Disconnect();
                    if (attempt >= 1)
                        throw new ElectrumException($"{endpoint.Name}: {(ex is OperationCanceledException ? "timed out" : ex.Message)}");
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_stream is not null)
            return;
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(endpoint.Host, endpoint.Port, cancellationToken);
            Stream stream = tcp.GetStream();
            if (endpoint.UseTls)
            {
                var ssl = new SslStream(stream, false, ValidateCertificate);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = endpoint.Host }, cancellationToken);
                stream = ssl;
            }
            _tcp = tcp;
            _stream = stream;
            _reader = new StreamReader(stream, new UTF8Encoding(false));
            await SendAsync("server.version", ["BTCPayServer-BitcoinBlake2b", ProtocolVersions], cancellationToken);
        }
        catch
        {
            tcp.Dispose();
            Disconnect();
            throw;
        }
    }

    bool ValidateCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (endpoint.PinnedCertificateSha256 is { } pin)
            return certificate is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), pin);
        return errors == SslPolicyErrors.None;
    }

    async Task<JToken> SendAsync(string method, object[] parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var request = JsonConvert.SerializeObject(new { jsonrpc = "2.0", id, method, @params = parameters }) + "\n";
        await _stream!.WriteAsync(Encoding.UTF8.GetBytes(request), cancellationToken);
        await _stream.FlushAsync(cancellationToken);
        while (true)
        {
            var line = await _reader!.ReadLineAsync(cancellationToken) ?? throw new IOException("Connection closed by the server");
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var message = JObject.Parse(line);
            if (message.Value<int?>("id") != id)
                continue; // a notification, or a stale answer
            if (message["error"] is JToken { Type: not JTokenType.Null } error)
                throw new ElectrumException($"{endpoint.Name}: {error["message"] ?? error}");
            return message["result"] ?? JValue.CreateNull();
        }
    }

    void Disconnect()
    {
        _reader?.Dispose();
        _stream?.Dispose();
        _tcp?.Dispose();
        _reader = null;
        _stream = null;
        _tcp = null;
    }

    public void Dispose()
    {
        Disconnect();
        _lock.Dispose();
    }
}
