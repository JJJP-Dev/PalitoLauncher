using System.Net;
using System.Net.NetworkInformation;
using PalitoLauncher.Core.Util;
using PalitoLauncher.Tests.TestHelpers.F3;

namespace PalitoLauncher.Tests;

public class NetworkInfoTests
{
    private const string Ipify = "api.ipify.org";
    private const string Amazon = "checkip.amazonaws.com";

    private static NetworkInfo.InterfaceAddress Candidate(
        string address,
        string name,
        bool hasGateway = false,
        string description = "Adaptador",
        NetworkInterfaceType type = NetworkInterfaceType.Ethernet,
        OperationalStatus status = OperationalStatus.Up) =>
        new(IPAddress.Parse(address), name, description, type, status, hasGateway);

    // ---------------------------------------------------------------- IPs locales

    [Theory]
    [InlineData("10.0.0.5", true)]
    [InlineData("10.255.255.255", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("172.15.0.1", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.34", true)]
    [InlineData("192.169.1.1", false)]
    [InlineData("26.12.0.8", false)]   // Radmin VPN
    [InlineData("8.8.8.8", false)]
    [InlineData("fd00::1", false)]     // solo IPv4
    public void IsPrivateIPv4_RecognizesPrivateRanges(string address, bool expected)
    {
        Assert.Equal(expected, NetworkInfo.IsPrivateIPv4(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", true)]
    [InlineData("Ethernet 3", "VirtualBox Host-Only Ethernet Adapter", true)]
    [InlineData("VMware Network Adapter VMnet8", "VMware Virtual Ethernet Adapter for VMnet8", true)]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz", false)]
    [InlineData("Ethernet", "Realtek PCIe GbE Family Controller", false)]
    [InlineData("Radmin VPN", "Famatech Radmin VPN Ethernet Adapter", false)]
    [InlineData(null, null, false)]
    public void IsVirtualAdapter_DetectsTypicalVirtualAdapters(string? name, string? description, bool expected)
    {
        Assert.Equal(expected, NetworkInfo.IsVirtualAdapter(name, description));
    }

    [Fact]
    public void SelectAndOrder_FiltersUselessAddresses()
    {
        var result = NetworkInfo.SelectAndOrder(new[]
        {
            Candidate("127.0.0.1", "Loopback Pseudo-Interface 1", type: NetworkInterfaceType.Loopback),
            Candidate("169.254.10.20", "Ethernet 2"),                                        // APIPA (sin DHCP)
            Candidate("192.168.1.50", "Ethernet 4", status: OperationalStatus.Down),        // desconectada
            Candidate("10.8.0.2", "Teredo", type: NetworkInterfaceType.Tunnel),             // túnel
            Candidate("0.0.0.0", "Raro"),
            new NetworkInfo.InterfaceAddress(IPAddress.Parse("fe80::1"), "Wi-Fi", "Intel", NetworkInterfaceType.Wireless80211, OperationalStatus.Up, true),
            Candidate("192.168.1.34", "Wi-Fi", hasGateway: true, type: NetworkInterfaceType.Wireless80211),
        });

        var only = Assert.Single(result);
        Assert.Equal(new LocalAddress("192.168.1.34", "Wi-Fi", true), only);
    }

    [Fact]
    public void SelectAndOrder_PrivateFirst_ThenGateway_ThenName_VirtualLast()
    {
        var result = NetworkInfo.SelectAndOrder(new[]
        {
            Candidate("172.24.160.1", "vEthernet (WSL)", description: "Hyper-V Virtual Ethernet Adapter"),
            Candidate("26.12.0.8", "Radmin VPN", hasGateway: true),
            Candidate("192.168.56.1", "Ethernet 3", description: "VirtualBox Host-Only Ethernet Adapter"),
            Candidate("10.0.0.7", "Zeta LAN"),
            Candidate("192.168.1.34", "Wi-Fi", hasGateway: true),
            Candidate("10.0.0.9", "Alfa LAN"),
            Candidate("25.1.2.3", "Hamachi"),
        });

        Assert.Equal(
            new[] { "192.168.1.34", "10.0.0.9", "10.0.0.7", "26.12.0.8", "25.1.2.3", "192.168.56.1", "172.24.160.1" },
            result.Select(a => a.Address));
        Assert.Equal(
            new[] { true, true, true, false, false, true, true },
            result.Select(a => a.IsPrivate));
        Assert.Equal("Wi-Fi", result[0].InterfaceName);
    }

    [Fact]
    public void SelectAndOrder_RemovesDuplicateAddresses()
    {
        var result = NetworkInfo.SelectAndOrder(new[]
        {
            Candidate("192.168.1.34", "Wi-Fi", hasGateway: true),
            Candidate("192.168.1.34", "Wi-Fi"),
        });

        Assert.Single(result);
    }

    [Fact]
    public void GetLocalIPv4Addresses_OnThisMachine_NeverThrowsAndReturnsOnlyUsefulIPv4()
    {
        var result = NetworkInfo.GetLocalIPv4Addresses();

        Assert.NotNull(result);
        foreach (var address in result)
        {
            var ip = IPAddress.Parse(address.Address);
            Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, ip.AddressFamily);
            Assert.False(IPAddress.IsLoopback(ip));
            Assert.False(address.Address.StartsWith("169.254.", StringComparison.Ordinal));
            Assert.Equal(NetworkInfo.IsPrivateIPv4(ip), address.IsPrivate);
            Assert.False(string.IsNullOrWhiteSpace(address.InterfaceName));
        }

        Assert.Equal(result.Count, result.Select(a => a.Address).Distinct().Count());
    }

    // ---------------------------------------------------------------- IP pública (sin red: handler falso)

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("  203.0.113.7\n", "203.0.113.7")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    [InlineData("2001:0DB8:0000::0001\r\n", "2001:db8::1")]
    [InlineData("1", null)]                 // IPAddress.TryParse lo aceptaría como 0.0.0.1
    [InlineData("1.2.3", null)]
    [InlineData("256.1.1.1", null)]
    [InlineData("01.2.3.4", null)]
    [InlineData("1.2.3.4 5.6.7.8", null)]
    [InlineData("127.0.0.1", null)]
    [InlineData("0.0.0.0", null)]
    [InlineData("255.255.255.255", null)]
    [InlineData("::1", null)]
    [InlineData("fe80::1%12", null)]
    [InlineData("<html><body>Error</body></html>", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParsePublicIp_AcceptsOnlyASingleValidAddress(string? text, string? expected)
    {
        Assert.Equal(expected, NetworkInfo.ParsePublicIp(text));
    }

    [Fact]
    public async Task GetPublicIpAsync_UsesIpifyFirst()
    {
        var handler = new CapturingHttpHandler(_ => CapturingHttpHandler.Text("203.0.113.7\n"));
        using var http = new HttpClient(handler);

        var ip = await NetworkInfo.GetPublicIpAsync(http, CancellationToken.None);

        Assert.Equal("203.0.113.7", ip);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://" + Ipify + "/", request.Uri.ToString());
    }

    [Theory]
    [InlineData("status")]
    [InlineData("garbage")]
    [InlineData("exception")]
    public async Task GetPublicIpAsync_FallsBackToAmazon(string failure)
    {
        var handler = new CapturingHttpHandler(request =>
        {
            if (request.Uri.Host != Ipify)
                return CapturingHttpHandler.Text("198.51.100.2\n");
            return failure switch
            {
                "status" => CapturingHttpHandler.Status(HttpStatusCode.ServiceUnavailable),
                "garbage" => CapturingHttpHandler.Text("<html>" + new string('x', 5000) + "</html>"),
                _ => throw new HttpRequestException("sin conexión"),
            };
        });
        using var http = new HttpClient(handler);

        var ip = await NetworkInfo.GetPublicIpAsync(http, CancellationToken.None);

        Assert.Equal("198.51.100.2", ip);
        Assert.Equal(new[] { Ipify, Amazon }, handler.Requests.Select(r => r.Uri.Host));
    }

    [Fact]
    public async Task GetPublicIpAsync_SlowServiceTimesOutAndFallsBack()
    {
        var handler = new CapturingHttpHandler(async (request, ct) =>
        {
            if (request.Uri.Host == Ipify)
                await Task.Delay(Timeout.Infinite, ct);
            return CapturingHttpHandler.Text("198.51.100.2");
        });
        using var http = new HttpClient(handler);

        var ip = await NetworkInfo.GetPublicIpAsync(http, TimeSpan.FromMilliseconds(200), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("198.51.100.2", ip);
    }

    [Fact]
    public async Task GetPublicIpAsync_EverythingFails_ReturnsNull()
    {
        var handler = new CapturingHttpHandler(_ => CapturingHttpHandler.Status(HttpStatusCode.InternalServerError));
        using var http = new HttpClient(handler);

        Assert.Null(await NetworkInfo.GetPublicIpAsync(http, CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetPublicIpAsync_AlreadyCancelled_Throws()
    {
        var handler = new CapturingHttpHandler(_ => CapturingHttpHandler.Text("203.0.113.7"));
        using var http = new HttpClient(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NetworkInfo.GetPublicIpAsync(http, cts.Token));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetPublicIpAsync_CancelledWhileWaiting_ThrowsInsteadOfFallingBack()
    {
        using var cts = new CancellationTokenSource();
        var handler = new CapturingHttpHandler(async (_, ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return CapturingHttpHandler.Text("203.0.113.7");
        });
        using var http = new HttpClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NetworkInfo.GetPublicIpAsync(http, cts.Token).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetPublicIpAsync_NullClient_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => NetworkInfo.GetPublicIpAsync(null!, CancellationToken.None));
    }
}
