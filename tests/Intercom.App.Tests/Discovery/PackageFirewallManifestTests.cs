using System.Xml.Linq;
using Xunit;

namespace Intercom.App.Tests.Discovery;

public sealed class PackageFirewallManifestTests
{
    const string Desktop2Namespace = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/2";

    [Fact]
    public void Manifest_InstallsPrivateLanFirewallRulesForDiscoveryAndControlChannel()
    {
        var manifestPath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Intercom.App", "Package.appxmanifest"));
        var document = XDocument.Load(manifestPath);
        XNamespace desktop2 = Desktop2Namespace;

        var firewallRules = Assert.Single(document.Descendants(desktop2 + "FirewallRules"));
        Assert.Equal("Intercom.App.exe", (string?)firewallRules.Attribute("Executable"));

        var rules = firewallRules.Elements(desktop2 + "Rule").ToList();
        Assert.Contains(rules, rule => IsInboundPrivateRule(rule, "UDP", "5353"));
        Assert.Contains(rules, rule => IsInboundPrivateRule(rule, "TCP", "47811"));
    }

    static bool IsInboundPrivateRule(XElement rule, string protocol, string port) =>
        (string?)rule.Attribute("Direction") == "in" &&
        (string?)rule.Attribute("IPProtocol") == protocol &&
        (string?)rule.Attribute("LocalPortMin") == port &&
        (string?)rule.Attribute("LocalPortMax") == port &&
        (string?)rule.Attribute("Profile") == "domainAndPrivate";
}
