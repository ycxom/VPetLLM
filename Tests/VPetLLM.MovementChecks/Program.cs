using System.Reflection;
using VPet_Simulator.Core;

var failures = new List<string>();

void Check(bool condition, string message)
{
    if (!condition)
    {
        failures.Add(message);
    }
}

var pluginAssembly = typeof(global::VPetLLM.VPetLLM).Assembly;
var policyType = pluginAssembly.GetType("VPetLLM.Core.Services.VPetMovementPolicy");

Check(policyType is not null, "VPetMovementPolicy must exist as the single movement-safety policy.");

if (policyType is not null)
{
    var isProtected = policyType.GetMethod(
        "IsAnimationProtected",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    var clampCoordinate = policyType.GetMethod(
        "ClampWindowCoordinate",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    Check(isProtected is not null, "IsAnimationProtected must be available.");
    Check(clampCoordinate is not null, "ClampWindowCoordinate must be available.");

    if (isProtected is not null)
    {
        Check((bool)isProtected.Invoke(null, [GraphInfo.GraphType.Move])!,
            "Move must be protected from plugin animation replacement.");
        Check((bool)isProtected.Invoke(null, [GraphInfo.GraphType.Raised_Dynamic])!,
            "Raised_Dynamic must remain protected.");
        Check(!(bool)isProtected.Invoke(null, [GraphInfo.GraphType.Default])!,
            "Default animation must remain interruptible.");
    }

    if (clampCoordinate is not null)
    {
        var rightEdgeAtZoom15 = (double)clampCoordinate.Invoke(null, [1420d, 0d, 1920d, 750d])!;
        Check(Math.Abs(rightEdgeAtZoom15 - 1170d) < 0.001,
            "A 750px window on a 1920px area must clamp to Left=1170.");

        var oversizedWindow = (double)clampCoordinate.Invoke(null, [500d, 0d, 400d, 500d])!;
        Check(Math.Abs(oversizedWindow) < 0.001,
            "A window larger than its move area must clamp to the area's start.");
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine("Movement regression checks failed:");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"- {failure}");
    }
    return 1;
}

Console.WriteLine("Movement regression checks passed.");
return 0;
