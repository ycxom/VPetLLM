# 过滤重复 DLL 的 PowerShell 脚本
param(
    [string]$OutputDir
)

Write-Host "开始过滤重复的 DLL 文件..." -ForegroundColor Green
Write-Host "输出目录: $OutputDir" -ForegroundColor Cyan

# 智能搜索 Steam 安装位置
function Find-SteamPath {
    $steamPaths = @()
    
    # 从注册表读取 Steam 安装路径
    try {
        $regPath = "HKCU:\Software\Valve\Steam"
        if (Test-Path $regPath) {
            $steamPath = (Get-ItemProperty -Path $regPath -Name "SteamPath" -ErrorAction SilentlyContinue).SteamPath
            if ($steamPath) {
                $basePath = $steamPath.Replace('/', '\')
                $steamPaths += $basePath
                
                # 从 libraryfolders.vdf 读取额外的库文件夹
                $vdfPath = Join-Path $basePath "steamapps\libraryfolders.vdf"
                if (Test-Path $vdfPath) {
                    try {
                        $content = Get-Content $vdfPath -Raw
                        $matches = [regex]::Matches($content, '"path"\s+"([^"]+)"')
                        foreach ($match in $matches) {
                            $libPath = $match.Groups[1].Value.Replace('\\\\', '\')
                            $steamPaths += $libPath
                        }
                    } catch {}
                }
            }
        }
    } catch {}
    
    return $steamPaths | Select-Object -Unique
}

# 搜索 VPet 安装目录
function Find-VPetPath {
    $steamPaths = Find-SteamPath
    
    foreach ($steamPath in $steamPaths) {
        $vpetPath = Join-Path $steamPath "steamapps\common\VPet"
        if (Test-Path $vpetPath) {
            Write-Host "找到 VPet 安装目录: $vpetPath" -ForegroundColor Green
            return $vpetPath
        }
    }
    
    return $null
}

$VPetDir = Find-VPetPath

# 检查 VPet 目录是否存在
if (-not $VPetDir) {
    Write-Host "警告: 未找到 VPet 主程序目录，跳过 DLL 过滤" -ForegroundColor Yellow
    Write-Host "提示: 请确保 VPet 已通过 Steam 安装" -ForegroundColor Yellow
    exit 0
}

Write-Host "VPet 主程序目录: $VPetDir" -ForegroundColor Cyan

# 获取 VPet 主程序目录下的所有 DLL 文件名（不含路径）
$vpetDlls = Get-ChildItem -Path $VPetDir -Filter "*.dll" -File | Select-Object -ExpandProperty Name
Write-Host "VPet 主程序包含 $($vpetDlls.Count) 个 DLL 文件" -ForegroundColor Cyan

# 获取输出目录下的所有 DLL 文件
$outputDlls = Get-ChildItem -Path $OutputDir -Filter "*.dll" -File -Recurse

$removedCount = 0
$keptCount = 0

foreach ($dll in $outputDlls) {
    if ($vpetDlls -contains $dll.Name) {
        Write-Host "删除重复 DLL: $($dll.Name)" -ForegroundColor Yellow
        Remove-Item $dll.FullName -Force
        $removedCount++
    } else {
        $keptCount++
    }
}

# 处理 SQLite 本地库 - 移动到 runtimes 子目录
$nativeLibs = @("e_sqlite3.dll")
$runtimesDir = Join-Path $OutputDir "runtimes\win-x64\native"

foreach ($nativeLib in $nativeLibs) {
    $sourcePath = Join-Path $OutputDir $nativeLib
    if (Test-Path $sourcePath) {
        # 创建目标目录
        if (-not (Test-Path $runtimesDir)) {
            New-Item -ItemType Directory -Path $runtimesDir -Force | Out-Null
        }
        
        $destPath = Join-Path $runtimesDir $nativeLib
        Write-Host "移动本地库: $nativeLib -> runtimes\win-x64\native\" -ForegroundColor Cyan
        Move-Item -Path $sourcePath -Destination $destPath -Force
    }
}

# 清理不需要的运行时目录（只保留 win-x64 和 win-x86）
Write-Host "`n清理不需要的运行时目录..." -ForegroundColor Yellow
$runtimesPath = Join-Path $OutputDir "runtimes"
if (Test-Path $runtimesPath) {
    $allowedRuntimes = @("win-x64", "win-x86")
    $allRuntimes = Get-ChildItem -Path $runtimesPath -Directory
    
    $removedRuntimes = 0
    foreach ($runtime in $allRuntimes) {
        if ($allowedRuntimes -notcontains $runtime.Name) {
            Write-Host "  删除运行时: $($runtime.Name)" -ForegroundColor Yellow
            Remove-Item -Path $runtime.FullName -Recurse -Force
            $removedRuntimes++
        }
    }
    
    if ($removedRuntimes -gt 0) {
        Write-Host "已删除 $removedRuntimes 个不需要的运行时目录" -ForegroundColor Green
    } else {
        Write-Host "没有需要删除的运行时目录" -ForegroundColor Green
    }
}

Write-Host "`n过滤完成!" -ForegroundColor Green
Write-Host "保留 DLL: $keptCount 个" -ForegroundColor Green
Write-Host "删除重复 DLL: $removedCount 个" -ForegroundColor Green
