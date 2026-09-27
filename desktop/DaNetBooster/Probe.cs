using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace DaNetBooster;

/// <summary>
/// Game-like latency probe: a small UDP DNS query to 1.1.1.1 every 500 ms, sent through the tunnel.
/// It takes the same path as game UDP (TUN, USB, phone, cell tower). ICMP ping can't cross a SOCKS tunnel.
/// Reports the RTT in ms, or null when there is no reply within 1 s (a lost packet).
/// </summary>
sealed class Probe : IDisposable
{
    public const string Target = "1.1.1.1";
    readonly CancellationTokenSource cts = new();
    public event Action<double?>? Sample;

    public Probe() => new Thread(Run) { IsBackground = true, Name = "probe" }.Start();

    void Run()
    {
        UdpClient? udp = null;
        ushort id = 0;
        var lostRun = 0;
        while (!cts.IsCancellationRequested)
        {
            // A socket opened before the adapter was rebuilt keeps sending into the old one: start fresh after 3 s of silence.
            if (udp == null || lostRun >= 6)
            {
                udp?.Dispose();
                udp = null;
                try
                {
                    udp = new UdpClient();
                    udp.Client.ReceiveTimeout = 1000;
                    udp.Connect(Target, 53); // throws when there is no route at all (cable pulled, no Wi-Fi)
                    lostRun = 0;
                }
                catch (SocketException)
                {
                    // Was outside any try: an unhandled exception on this thread killed the whole app.
                    udp?.Dispose();
                    udp = null;
                    if (cts.IsCancellationRequested) break;
                    Sample?.Invoke(null);
                    cts.Token.WaitHandle.WaitOne(500);
                    continue;
                }
            }
            id++;
            var sw = Stopwatch.StartNew();
            double? rtt = null;
            try
            {
                udp.Send(Query(id));
                while (sw.ElapsedMilliseconds < 1000)
                {
                    IPEndPoint? from = null;
                    var r = udp.Receive(ref from);
                    if (r.Length >= 2 && (r[0] << 8 | r[1]) == id) { rtt = sw.Elapsed.TotalMilliseconds; break; }
                    // else: a late reply to an earlier (already counted lost) query
                }
            }
            catch (SocketException) { }
            lostRun = rtt == null ? lostRun + 1 : 0;
            if (cts.IsCancellationRequested) break;
            try { Sample?.Invoke(rtt); } catch (Exception) { break; } // the window is gone: stop quietly
            var wait = 500 - (int)sw.ElapsedMilliseconds;
            if (wait > 0) cts.Token.WaitHandle.WaitOne(wait);
        }
        udp?.Dispose();
    }

    /// <summary>
    /// DNS query for the root zone's NS records. 1.1.1.1 always has them cached (TTL 6 days), so it answers without
    /// any lookup of its own: RTT = network RTT. Measured: an A query for cloudflare.com added ~10 ms of resolver
    /// time to every sample, and a cache miss on it could add ~250 ms that had nothing to do with the connection.
    /// </summary>
    static byte[] Query(ushort id) =>
    [
        (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0,
        0,          // root name "."
        0, 2, 0, 1, // type NS, class IN
    ];

    public void Dispose() => cts.Cancel();
}
