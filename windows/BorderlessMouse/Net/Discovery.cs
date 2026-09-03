using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using BorderlessMouse.Protocol;

namespace BorderlessMouse.Net;

public sealed record DiscoveredPeer(string Name, string Address, int Port)
{
    public string Display => $"{Name}  ·  {Address}";
}

/// <summary>Wysyła broadcast "BLM2?" i zbiera odpowiedzi Maców.</summary>
public sealed class DiscoveryClient : IDisposable
{
    public event Action<DiscoveredPeer>? PeerFound;

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;

    public void Start()
    {
        Stop();
        var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        SocketHelpers.DisableUdpConnReset(udp.Client);
        _udp = udp;
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = Task.Run(() => ReceiveLoop(udp, cts.Token));
        _ = Task.Run(() => BroadcastLoop(udp, cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        try { _udp?.Close(); } catch { /* ignore */ }
        _udp = null;
    }

    public void Dispose() => Stop();

    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        yield return IPAddress.Broadcast;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                var bc = new byte[4];
                for (var i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | (byte)~mask[i]);
                yield return new IPAddress(bc);
            }
        }
    }

    private static async Task BroadcastLoop(UdpClient udp, CancellationToken ct)
    {
        var payload = Encoding.ASCII.GetBytes(ProtocolConstants.DiscoveryRequest);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (var addr in BroadcastAddresses().Distinct())
                {
                    try { await udp.SendAsync(payload, payload.Length, new IPEndPoint(addr, ProtocolConstants.DiscoveryPort)).ConfigureAwait(false); }
                    catch { /* interfejs bez broadcastu */ }
                }
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task ReceiveLoop(UdpClient udp, CancellationToken ct)
    {
        var reply = Encoding.ASCII.GetBytes(ProtocolConstants.DiscoveryReply);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try { result = await udp.ReceiveAsync(ct).ConfigureAwait(false); }
                catch (SocketException) { continue; }
                var data = result.Buffer;
                if (data.Length < reply.Length + 2 || !data.AsSpan(0, reply.Length).SequenceEqual(reply)) continue;
                var port = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(reply.Length));
                var name = Encoding.UTF8.GetString(data, reply.Length + 2, data.Length - reply.Length - 2);
                var address = result.RemoteEndPoint.Address.ToString();
                PeerFound?.Invoke(new DiscoveredPeer(string.IsNullOrWhiteSpace(name) ? address : name, address, port));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }
}

internal static class SocketHelpers
{
    /// <summary>Windows: ICMP "port unreachable" zamyka odbiór UDP wyjątkiem – wyłączamy to.</summary>
    public static void DisableUdpConnReset(Socket socket)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
        }
        catch
        {
            // nie każdy stos to wspiera
        }
    }
}
