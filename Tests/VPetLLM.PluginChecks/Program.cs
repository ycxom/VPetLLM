using System.Reflection;

var failures = new List<string>();
var pluginAssembly = typeof(global::VPetLLM.VPetLLM).Assembly;
var pluginManagerType = pluginAssembly.GetType("VPetLLM.Utils.Plugin.PluginManager");
var getParallelism = pluginManagerType?.GetMethod(
    "GetLoadParallelism",
    BindingFlags.Static | BindingFlags.NonPublic);

if (getParallelism is null)
{
    failures.Add("PluginManager must expose its load parallelism policy for regression checks.");
}
else
{
    var emptyDirectoryParallelism = (int)getParallelism.Invoke(null, [0])!;
    if (emptyDirectoryParallelism < 1)
    {
        failures.Add("An empty plugin directory must never produce zero load parallelism.");
    }

    try
    {
        _ = new ParallelOptions { MaxDegreeOfParallelism = emptyDirectoryParallelism };
    }
    catch (ArgumentOutOfRangeException)
    {
        failures.Add("The calculated load parallelism must be accepted by ParallelOptions.");
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine("Plugin regression checks failed:");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"- {failure}");
    }
    return 1;
}

Console.WriteLine("Plugin regression checks passed.");
return 0;
