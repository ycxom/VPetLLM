using System.Reflection;
using VPetLLM.Services;

var failures = new List<string>();

void Check(bool condition, string message)
{
    if (!condition)
    {
        failures.Add(message);
    }
}

var pluginAssembly = typeof(global::VPetLLM.VPetLLM).Assembly;
var serviceType = pluginAssembly.GetType("VPetLLM.Services.VersionCheckService");
Check(serviceType is not null, "VersionCheckService must exist.");

if (serviceType is not null)
{
    var parseVersion = serviceType.GetMethod(
        "ParseVersion",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    var isUpdateAvailable = serviceType.GetMethod(
        "IsUpdateAvailable",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    Check(parseVersion is not null, "ParseVersion must be available.");
    Check(isUpdateAvailable is not null, "IsUpdateAvailable must be available.");

    if (parseVersion is not null)
    {
        const string info = "vupmod#VPetLLM:|author#ycxom:|gamever#11068:|ver#21201:|\nintro#VPetLLM_Description:|";
        var parsed = parseVersion.Invoke(null, [info]);
        var numericValue = parsed?.GetType().GetProperty("NumericValue")?.GetValue(parsed);
        var displayText = parsed?.GetType().GetProperty("DisplayText")?.GetValue(parsed)?.ToString();

        Check(numericValue is 21201, "The ver field must parse as 21201, not gamever 11068.");
        Check(displayText == "2.12.01", "Version 21201 must display as 2.12.01.");
        Check(parseVersion.Invoke(null, ["gamever#11068:|"]) is null,
            "Missing ver field must not be treated as a plugin version.");
    }

    if (isUpdateAvailable is not null)
    {
        Check((bool)isUpdateAvailable.Invoke(null, [21201, 21202])!,
            "A larger remote version must be reported as an update.");
        Check(!(bool)isUpdateAvailable.Invoke(null, [21201, 21201])!,
            "The same version must be reported as current.");
        Check(!(bool)isUpdateAvailable.Invoke(null, [21202, 21201])!,
            "An older remote version must not be reported as an update.");
    }
}

if (args.Contains("--integration", StringComparer.OrdinalIgnoreCase))
{
    var service = new VersionCheckService(new global::VPetLLM.Setting.PluginStoreSetting
    {
        UseProxy = false
    });
    var remoteResult = await service.CheckAsync();
    Check(remoteResult.Succeeded,
        $"Remote info.lps must be reachable and parseable: {remoteResult.ErrorMessage}");
    Check(remoteResult.LatestVersion?.NumericValue is > 0,
        "Remote info.lps must provide a positive plugin version.");
}

if (failures.Count > 0)
{
    Console.Error.WriteLine("Version regression checks failed:");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"- {failure}");
    }
    return 1;
}

Console.WriteLine("Version regression checks passed.");
return 0;
