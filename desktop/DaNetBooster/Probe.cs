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
                udp = new UdpClient();
                udp.Client.ReceiveTimeout = 1000;
                udp.Connect(Target, 53);
                lostRun = 0;
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
            Sample?.Invoke(rtt);
            var wait = 500 - (int)sw.ElapsedMilliseconds;
            if (wait > 0) cts.Token.WaitHandle.WaitOne(wait);
        }
        udp?.Dispose();
    }

    /// <summary>DNS A query for cloudflare.com (answer is cached at 1.1.1.1, so RTT ≈ network RTT).</summary>
    static byte[] Query(ushort id) =>
    [
        (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0,
        10, .."cloudflare"u8, 3, .."com"u8, 0,
        0, 1, 0, 1,
    ];

    public void Dispose() => cts.Cancel();
}
