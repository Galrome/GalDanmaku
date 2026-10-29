using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace GalDanmaku.Tests;

internal sealed class LoopbackWebSocketServer : IDisposable
{
    private readonly TcpListener _listener;
    public LoopbackWebSocketServer(bool start = true)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        if (!start)
        {
            _listener.Stop();
            _listener = new TcpListener(IPAddress.Loopback, Port);
        }
    }
    public int Port { get; }
    public string Endpoint => $"ws://127.0.0.1:{Port}/api/ws/text/origin";
    public void Start() => _listener.Start();
    public void Dispose() => _listener.Stop();

    public async Task<(TcpClient Client, WebSocket Socket)> AcceptAsync(CancellationToken token)
    {
        TcpClient client = await _listener.AcceptTcpClientAsync(token);
        try
        {
            NetworkStream stream = client.GetStream();
            var bytes = new List<byte>();
            byte[] single = new byte[1];
            while (bytes.Count < 8192)
            {
                Assert.Equal(1, await stream.ReadAsync(single, token));
                bytes.Add(single[0]);
                if (bytes.Count >= 4 && bytes.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray())) break;
            }
            string request = Encoding.ASCII.GetString(bytes.ToArray());
            Assert.StartsWith("GET /api/ws/text/origin HTTP/1.1\r\n", request);
            string key = request.Split("\r\n").Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
            string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), token);
            return (client, WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(30)));
        }
        catch { client.Dispose(); throw; }
    }

    public async Task<TcpClient> AcceptWithoutHandshakeAsync(CancellationToken token) => await _listener.AcceptTcpClientAsync(token);
}
