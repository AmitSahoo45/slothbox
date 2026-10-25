using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SlothBox.Core;
using SlothBox.Server.Api;
using SlothBox.Server.Phone;

namespace SlothBox.Tests.Api.Settings;

// The phone block on the Folders page (spec 7.4.6, 7.9, 8.2): the link, its QR code, the firewall rule and New link.
public sealed class PhoneEndpointTests
{
    private static readonly IPAddress Lan = IPAddress.Parse("192.168.1.23");

    private static ApiServer Server(IPAddress? lan, IFirewallRules? firewall = null) => new(services =>
        services.AddSingleton(sp => new PhoneLink(sp.GetRequiredService<ISettingsStore>(), firewall, () => lan)));

    [Fact]
    public async Task The_link_carries_the_lan_address_the_port_and_the_token_with_its_qr_code()
    {
        await using var server = Server(Lan);

        var phone = await server.GetAsync<PhoneDto>("/api/phone");

        (phone.Enabled, phone.Url, phone.FirewallRulePresent)
            .ShouldBe((true, "http://192.168.1.23:5081/u/synthetic-phone-token", false));
        phone.QrSvg.ShouldNotBeNull().ShouldStartWith("<svg");
        phone.QrSvg.ShouldContain("viewBox");
    }

    [Fact]
    public async Task The_link_uses_the_port_phones_are_served_on_whatever_settings_say()
    {
        await using var server = Server(Lan);
        server.Settings.Current = server.Settings.Current with
        {
            Phone = server.Settings.Current.Phone with { Port = 5090 }
        };

        (await server.GetAsync<PhoneDto>("/api/phone")).Url
            .ShouldBe("http://192.168.1.23:5081/u/synthetic-phone-token");
    }

    [Fact]
    public async Task The_firewall_rule_is_found_by_its_display_name()
    {
        var firewall = new FakeFirewall("SlothBox phone page");
        await using var server = Server(Lan, firewall);

        (await server.GetAsync<PhoneDto>("/api/phone")).FirewallRulePresent.ShouldBeTrue();
        firewall.Asked.ShouldBe(["SlothBox phone page"]);
    }

    [Fact]
    public async Task Without_a_private_lan_address_there_is_no_link()
    {
        await using var server = Server(null);

        var phone = await server.GetAsync<PhoneDto>("/api/phone");

        (phone.Enabled, phone.Url, phone.QrSvg).ShouldBe((true, null, null));
    }

    [Fact]
    public async Task A_switched_off_phone_page_has_no_link()
    {
        await using var server = Server(Lan);
        server.Settings.Current = server.Settings.Current with
        {
            Phone = server.Settings.Current.Phone with { Enabled = false }
        };

        var phone = await server.GetAsync<PhoneDto>("/api/phone");

        (phone.Enabled, phone.Url, phone.QrSvg).ShouldBe((false, null, null));
    }

    [Fact]
    public async Task New_link_saves_a_new_token_so_the_old_link_stops_working()
    {
        await using var server = Server(Lan);
        var before = await server.GetAsync<PhoneDto>("/api/phone");

        using var response = await server.SendAsync(HttpMethod.Post, "/api/phone/rotate");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = await response.ReadAsync<PhoneDto>();
        var token = server.Settings.Current.Phone.Token;
        token.ShouldNotBe("synthetic-phone-token");
        after.Url.ShouldBe($"http://192.168.1.23:5081/u/{token}");
        after.QrSvg.ShouldNotBe(before.QrSvg);
        (await server.GetAsync<PhoneDto>("/api/phone")).ShouldBe(after);
    }

    [Theory]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("192.168.1.23", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("169.254.10.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("fd00::1", false)]
    public void Only_private_ipv4_addresses_are_used_for_the_link(string address, bool usable) =>
        PhoneLink.IsPrivate(IPAddress.Parse(address)).ShouldBe(usable);
}
