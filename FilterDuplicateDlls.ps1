param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDir
)

$ErrorActionPreference = "Stop"
$outputRoot = [IO.Path]::GetFullPath($OutputDir)
$outputLeaf = Split-Path -Leaf $outputRoot
$outputParentLeaf = Split-Path -Leaf (Split-Path -Parent $outputRoot)
if ($outputLeaf -ne "plugin" -or $outputParentLeaf -ne "3000_VPetLLM") {
    throw "Refusing cleanup outside the expected 3000_VPetLLM\plugin directory: $outputRoot"
}
if (-not (Test-Path -LiteralPath $outputRoot -PathType Container)) {
    Write-Host "Plugin output directory does not exist: $outputRoot" -ForegroundColor Yellow
    exit 0
}

# These assemblies are supplied by the VPet host. Shipping private copies in a
# mod can cause assembly load-order conflicts and version skew.
$hostManagedDlls = @(
    "LinePutScript.dll",
    "LinePutScript.Localization.WPF.dll",
    "NAudio.dll",
    "NAudio.Asio.dll",
    "NAudio.Core.dll",
    "NAudio.Midi.dll",
    "NAudio.Wasapi.dll",
    "NAudio.WinForms.dll",
    "NAudio.WinMM.dll",
    "Panuon.WPF.dll",
    "Panuon.WPF.UI.dll",
    "SkiaSharp.dll",
    "VPet-Simulator.Core.dll",
    "VPet-Simulator.Windows.Interface.dll"
)

$removed = 0
foreach ($name in $hostManagedDlls) {
    $candidate = Join-Path $outputRoot $name
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        Remove-Item -LiteralPath $candidate -Force
        Write-Host "Removed host assembly: $name" -ForegroundColor Yellow
        $removed++
    }
}

# The private payload is architecture-specific and must only live below
# runtimes\win-*\native. Remove stale copies produced by older build rules.
$misplacedPayload = Join-Path $outputRoot "VPetLLM.SecureCommunication.dll"
if (Test-Path -LiteralPath $misplacedPayload -PathType Leaf) {
    Remove-Item -LiteralPath $misplacedPayload -Force
    Write-Host "Removed misplaced root native payload." -ForegroundColor Yellow
    $removed++
}

$runtimesRoot = Join-Path $outputRoot "runtimes"
if (Test-Path -LiteralPath $runtimesRoot -PathType Container) {
    foreach ($directory in Get-ChildItem -LiteralPath $runtimesRoot -Directory) {
        if ($directory.Name -notin @("win-x64", "win-x86")) {
            Remove-Item -LiteralPath $directory.FullName -Recurse -Force
            Write-Host "Removed non-Windows runtime: $($directory.Name)" -ForegroundColor Yellow
            $removed++
        }
    }

    # VPet also owns the native SkiaSharp runtime.
    foreach ($runtime in @("win-x64", "win-x86")) {
        $skiaNative = Join-Path $runtimesRoot "$runtime\native\libSkiaSharp.dll"
        if (Test-Path -LiteralPath $skiaNative -PathType Leaf) {
            Remove-Item -LiteralPath $skiaNative -Force
            Write-Host "Removed host native library: $runtime\native\libSkiaSharp.dll" -ForegroundColor Yellow
            $removed++
        }
    }
}

Write-Host "Host dependency cleanup complete. Removed items: $removed" -ForegroundColor Green
