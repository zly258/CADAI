#Requires -Version 7.0

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$NoDeploy,
    [switch]$PerUser,
    [switch]$Clean,
    [switch]$NoTests,
    [switch]$Publish
)

$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
if (-not $root) {
    throw "Cannot determine repository root. Run build.ps1 from the checked-out repository."
}

$binDir = "$root\bin"
$buildDir = "$root\.build"
$distDir = "$root\dist"

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "================================================================================" -ForegroundColor Cyan
    Write-Host "  $Message" -ForegroundColor Cyan
    Write-Host "================================================================================" -ForegroundColor Cyan
}

if ($Clean) {
    Write-Step "Cleaning Build Outputs"
    if (Test-Path $binDir) { Remove-Item $binDir -Recurse -Force }
    if (Test-Path $buildDir) { Remove-Item $buildDir -Recurse -Force }
    if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
}

# 1. 构建核心层与弹窗看门狗
Write-Step "Building Core & ModalAgent (CadAI.Core & CadAI.ModalAgent)"
& dotnet build "$root\src\CadAI.Core\CadAI.Core.csproj" -c $Configuration --nologo -v:m
if ($LASTEXITCODE -ne 0) { throw "CadAI.Core build failed." }

& dotnet build "$root\src\CadAI.ModalAgent\CadAI.ModalAgent.csproj" -c $Configuration --nologo -v:m
if ($LASTEXITCODE -ne 0) { throw "CadAI.ModalAgent build failed." }

# 2. 构建 AutoCAD MCP 工具引擎与宿主插件（双目标框架：net48 支持 2019-2024，net8.0-windows 支持 2025-2026）
Write-Step "Building AutoCAD MCP & Host Plugin (2019-2026)"
& dotnet build "$root\src\CadAI.Mcp.AutoCAD\CadAI.Mcp.AutoCAD.csproj" -c $Configuration --nologo -v:m
if ($LASTEXITCODE -ne 0) { throw "CadAI.Mcp.AutoCAD build failed." }

& dotnet build "$root\src\CadAI.Host.AutoCAD\CadAI.Host.AutoCAD.csproj" -c $Configuration --nologo -v:m
if ($LASTEXITCODE -ne 0) { throw "CadAI.Host.AutoCAD build failed." }

# 3. 拷贝 ModalAgent 到两个目标框架的交付目录
$modalAgentSrc = "$buildDir\bin\CadAI.ModalAgent"
if (Test-Path $modalAgentSrc) {
    $cadModalCfg = "$root\src\CadAI.Host.AutoCAD\DwgChat\ModalAgent\modal-agent.json"
    foreach ($tfm in @("net48", "net8.0-windows")) {
        $cadTfmBin = "$binDir\AutoCADAI\$tfm"
        if (Test-Path $cadTfmBin) {
            $cadModalDir = "$cadTfmBin\ModalAgent"
            if (Test-Path $cadModalDir) { Remove-Item $cadModalDir -Recurse -Force }
            New-Item -ItemType Directory -Path $cadModalDir -Force | Out-Null
            Copy-Item "$modalAgentSrc\*" $cadModalDir -Force -Recurse
            if (Test-Path $cadModalCfg) {
                Copy-Item $cadModalCfg $cadModalDir -Force
                Copy-Item $cadModalCfg "$cadModalDir\modal-agent.dwgai.json" -Force
            }
        }
    }
}

# 4. Release 产物清理 PDB
if ($Configuration -eq "Release" -and (Test-Path "$binDir\AutoCADAI")) {
    Get-ChildItem "$binDir\AutoCADAI" -Filter "*.pdb" -Recurse -File -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

# 5. 运行单元测试
if (-not $NoTests) {
    Write-Step "Running Unit Tests"
    & dotnet test "$root\src\CadAI.Tests\CadAI.Tests.csproj" -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed." }
}

# 6. 自动部署 CADAI.bundle 到本机受 SECURELOAD=1 信任的系统级 AutoCAD 插件目录（参考 SteelStructureSuite）
if (-not $NoDeploy) {
    Write-Step "Deploying CADAI.bundle to AutoCAD ApplicationPlugins"
    $targetBundleName = "CADAI.bundle"
    $installRootSystem = "$env:ProgramFiles\Autodesk\ApplicationPlugins"
    $bundleSystem  = Join-Path $installRootSystem $targetBundleName
    $bundlePerUser = "$env:APPDATA\Autodesk\ApplicationPlugins\$targetBundleName"
    $targetBundle  = if ($PerUser) { $bundlePerUser } else { $bundleSystem }

    # 清理其它插件根目录下的同名 CADAI.bundle，防止多路径双加载
    $allPluginRoots = @(
        "$env:ProgramFiles\Autodesk\ApplicationPlugins",
        "${env:ProgramFiles(x86)}\Autodesk\ApplicationPlugins",
        "$env:ProgramData\Autodesk\ApplicationPlugins",
        "$env:APPDATA\Autodesk\ApplicationPlugins"
    )
    foreach ($pr in $allPluginRoots) {
        $other = Join-Path $pr $targetBundleName
        if ($other -ne $targetBundle -and (Test-Path -LiteralPath $other)) {
            Remove-Item -LiteralPath $other -Recurse -Force -ErrorAction SilentlyContinue
            Write-Host "==> Removed duplicate bundle: $other" -ForegroundColor Yellow
        }
    }

    # 清理 AutoCAD 注册表中的 CADAI 缓存项
    $acadRegRoot = 'HKCU:\Software\Autodesk\AutoCAD'
    if (Test-Path $acadRegRoot) {
        Get-ChildItem $acadRegRoot -ErrorAction SilentlyContinue | ForEach-Object {
            Get-ChildItem $_.PSPath -ErrorAction SilentlyContinue | ForEach-Object {
                foreach ($sub in @('Applications', 'Loaded')) {
                    $subPath = Join-Path $_.PSPath $sub
                    if (Test-Path $subPath) {
                        Get-ChildItem $subPath -ErrorAction SilentlyContinue |
                            Where-Object { $_.PSChildName -match '^CADAI' } |
                            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
                    }
                }
            }
        }
    }

    $deployBundle = {
        param([string]$destBundle)
        if (Test-Path -LiteralPath $destBundle) { Remove-Item -LiteralPath $destBundle -Recurse -Force }
        New-Item -ItemType Directory -Path $destBundle -Force | Out-Null
        $bundleContents = "$destBundle\Contents"
        Copy-Item "$root\src\CadAI.Host.AutoCAD\PackageContents.xml" $destBundle -Force
        New-Item -ItemType Directory -Path $bundleContents -Force | Out-Null
        Get-ChildItem "$binDir\AutoCADAI" | Copy-Item -Destination $bundleContents -Recurse -Force
    }

    try {
        & $deployBundle $targetBundle
        Write-Host "==> Installed AutoCAD Bundle: $targetBundle (AutoCAD 2019-2026)" -ForegroundColor Green
    } catch {
        Write-Host "Failed to deploy to Program Files ($targetBundle). Retrying for current user (%APPDATA%)..." -ForegroundColor Yellow
        & $deployBundle $bundlePerUser
        Write-Host "==> Installed AutoCAD Bundle (PerUser): $bundlePerUser (AutoCAD 2019-2026)" -ForegroundColor Green
    }
}

# 7. 发布标准交付包（dist\CADAI.bundle 与 dist\CADAI-v<Version>-AutoCAD2019-2026.zip）
if ($Publish) {
    Write-Step "Publishing Release DLLs & CADAI.bundle (AutoCAD 2019-2026)"
    $buildPropsPath = "$root\Directory.Build.props"
    $projectVersion = ([xml](Get-Content -LiteralPath $buildPropsPath -Raw)).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
    $projectVersion = if ($projectVersion -is [System.Xml.XmlElement]) { $projectVersion.InnerText } else { $projectVersion }
    if (-not $projectVersion) { $projectVersion = "1.0.0" }

    $bundleOut = "$distDir\CADAI.bundle"
    $contentsOut = "$bundleOut\Contents"
    if (Test-Path $bundleOut) { Remove-Item $bundleOut -Recurse -Force }
    New-Item -ItemType Directory -Path $contentsOut -Force | Out-Null

    Copy-Item "$root\src\CadAI.Host.AutoCAD\PackageContents.xml" $bundleOut -Force
    Get-ChildItem "$binDir\AutoCADAI" | Copy-Item -Destination $contentsOut -Recurse -Force

    $zipPath = "$distDir\CADAI-v$projectVersion-AutoCAD2019-2026.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path $bundleOut -DestinationPath $zipPath -CompressionLevel Optimal

    Write-Host "==> Bundle directory: $bundleOut" -ForegroundColor Green
    Write-Host "==> Release archive : $zipPath" -ForegroundColor Green
}

Write-Host ""
Write-Host "CADAI Build Finished Successfully." -ForegroundColor Green
