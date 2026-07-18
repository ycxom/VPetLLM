using System.Reflection;
using System.Xml.Linq;

var failures = new List<string>();

void Check(bool condition, string message)
{
    if (!condition)
    {
        failures.Add(message);
    }
}

var pluginAssembly = typeof(global::VPetLLM.VPetLLM).Assembly;
var routingPolicyType = pluginAssembly.GetType("VPetLLM.Core.Services.ProxyRoutingPolicy");
var healthPolicyType = pluginAssembly.GetType("VPetLLM.Core.Services.ProxyHealthPolicy");

Check(routingPolicyType is not null,
    "ProxyRoutingPolicy must keep plugin-store routing independent from the main proxy.");
Check(healthPolicyType is not null,
    "ProxyHealthPolicy must aggregate multiple connectivity probes.");

if (routingPolicyType is not null)
{
    var resolve = routingPolicyType.GetMethod(
        "ResolvePluginStore",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    Check(resolve is not null, "ResolvePluginStore must be available.");

    if (resolve is not null)
    {
        var disabled = resolve.Invoke(null, [false, "https://ghfast.top"]);
        var enabled = resolve.Invoke(null, [true, "https://ghfast.top"]);
        var disabledMode = disabled?.GetType().GetProperty("Mode")?.GetValue(disabled)?.ToString();
        var enabledMode = enabled?.GetType().GetProperty("Mode")?.GetValue(enabled)?.ToString();

        Check(disabledMode == "Direct",
            "Plugin store must stay direct when its own switch is off, regardless of main proxy settings.");
        Check(enabledMode == "UrlRewrite",
            "Plugin store must use its own mirror when its own switch is on.");

        var localProxy = resolve.Invoke(null, [true, "127.0.0.1:7890"]);
        var localProxyMode = localProxy?.GetType().GetProperty("Mode")?.GetValue(localProxy)?.ToString();
        Check(localProxyMode == "HttpProxy",
            "Plugin store must continue supporting an explicit host:port proxy.");
    }
}

if (healthPolicyType is not null)
{
    var isAvailable = healthPolicyType.GetMethod(
        "IsAvailable",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    Check(isAvailable is not null, "IsAvailable must be available.");

    if (isAvailable is not null)
    {
        Check((bool)isAvailable.Invoke(null, [new[] { false, true, false }])!,
            "One successful endpoint must prove that the proxy is available.");
        Check(!(bool)isAvailable.Invoke(null, [new[] { false, false, false }])!,
            "All attempted endpoints failing must report the proxy unavailable.");
    }

    var endpoints = healthPolicyType.GetField(
        "ProbeEndpoints",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) as Array;
    Check(endpoints is { Length: >= 2 },
        "Proxy health checks must not depend on a single external endpoint.");
}

var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var settingsXaml = XDocument.Load(Path.Combine(repositoryRoot, "UI", "Windows", "winSettingNew.xaml"));
XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
var pluginStoreToggle = settingsXaml.Descendants()
    .FirstOrDefault(element => (string?)element.Attribute(x + "Name") == "CheckBox_PluginStore_UseProxy");
Check(pluginStoreToggle is not null, "Plugin-store proxy toggle must exist.");
if (pluginStoreToggle is not null)
{
    Check(pluginStoreToggle.Attribute("IsEnabled") is null,
        "Plugin-store proxy toggle must not be disabled by the main proxy switch.");
    Check(!pluginStoreToggle.Ancestors().Attributes("IsEnabled")
            .Any(attribute => attribute.Value.Contains("CheckBox_Proxy_IsEnabled", StringComparison.Ordinal)),
        "Plugin-store proxy settings must not be nested in the main proxy enabled panel.");
}

if (failures.Count > 0)
{
    Console.Error.WriteLine("Proxy regression checks failed:");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"- {failure}");
    }
    return 1;
}

Console.WriteLine("Proxy regression checks passed.");
return 0;
