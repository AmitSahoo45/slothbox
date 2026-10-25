using System.Net;
using System.Net.Sockets;
using QRCoder;
using SlothBox.Core;
using SlothBox.Server.Api;
using SlothBox.Server.Settings;

namespace SlothBox.Server.Phone;

// The phone block on the Folders page (spec 7.4.6, 7.9): the phone page's address on the Wi-Fi, its QR code, whether
// the firewall lets phones in, and New link. lanAddress: tests pass their own.
public sealed class PhoneLink(ISettingsStore settings, IFirewallRules? firewall = null,
    Func<IPAddress?>? lanAddress = null)
{
    // The display name of the rule from the README setup (spec 15 step 5).
    public const string FirewallRuleName = "SlothBox phone page";

    // The port the phone listener has (ServerHost, ApiProtection), whatever settings.json says.
    private const int ListenerPort = 5081;

    // A documentation address (TEST-NET-1): only the routing table is asked about it, nothing is sent there.
    private static readonly IPEndPoint RouteProbe = new(IPAddress.Parse("192.0.2.1"), 9);

    private readonly Func<IPAddress?> _lanAddress = lanAddress ?? DefaultRouteAddress;

    // No link while the phone page is off or the laptop has no private address to offer.
    public PhoneDto Get()
    {
        var phone = settings.Current.Phone;
        var address = phone.Enabled ? _lanAddress() : null;
        var url = address is null ? null : $"http://{address}:{ListenerPort}/u/{phone.Token}";
        return new PhoneDto(phone.Enabled, url, url is null ? null : QrSvg(url),
            firewall?.Exists(FirewallRuleName) ?? false);
    }

    // New link: the old link stops working at once, as the page checks the token against the saved settings.
    public PhoneDto Rotate()
    {
        settings.Update(current => current with
        {
            Phone = current.Phone with { Token = SettingsFile.NewPhoneToken() }
        });
        return Get();
    }

    // 10/8, 172.16/12 and 192.168/16: the addresses a home Wi-Fi hands out. Anything else is not offered.
    public static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168;
    }

    // Spec 7.9 "LAN address": the private IPv4 address of the interface with the default route. Connecting a UDP socket
    // sends nothing; it only makes the system pick the local address it would send from.
    private static IPAddress? DefaultRouteAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(RouteProbe);
            return socket.LocalEndPoint is IPEndPoint { Address: var address } && IsPrivate(address) ? address : null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    // Scales to the box the page gives it (viewBox), with a quiet zone so phones read it from a screen.
    private static string QrSvg(string url)
    {
        using var data = QRCodeGenerator.GenerateQrCode(url, QRCodeGenerator.ECCLevel.M);
        using var svg = new SvgQRCode(data);
        return svg.GetGraphic(8, "#000000", "#ffffff", true, SvgQRCode.SizingMode.ViewBoxAttribute);
    }
}
