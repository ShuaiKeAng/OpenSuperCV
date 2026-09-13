[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:\.\d+)?$')]
    [string]$Version = '1.0.0',

    [switch]$SkipPublish,

    [switch]$BundleRuntime,

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$RuntimeVersion = '8.0.29',

    [ValidatePattern('^[A-Fa-f0-9]{64}$')]
    [string]$RuntimeSha256 = 'C0FFA16EFEB7EF3AC8100A6A9D7089D9C2904EE89F1815557A79A91BE584F775'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src\SuperCV.Presentation.Wpf\SuperCV.Presentation.Wpf.csproj'
$publishDir = Join-Path $repoRoot 'artifacts\publish\win-x64'
$installerOutputDir = Join-Path $repoRoot 'artifacts\installer'
$installerScript = Join-Path $PSScriptRoot 'SuperCV.iss'
$packageSuffix = if ($BundleRuntime) { '-Full' } else { '' }
$expectedInstaller = Join-Path $installerOutputDir "SuperCV-Setup-$Version$packageSuffix.exe"
$runtimeInstaller = Join-Path $repoRoot "artifacts\prerequisites\windowsdesktop-runtime-$RuntimeVersion-win-x64.exe"

function Find-InnoSetupCompiler {
    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $candidatePaths = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )

    foreach ($candidatePath in $candidatePaths) {
        if (Test-Path -LiteralPath $candidatePath -PathType Leaf) {
            return $candidatePath
        }
    }

    throw @'
未找到 Inno Setup 6 编译器（ISCC.exe）。
请先安装 Inno Setup：
  winget install --id JRSoftware.InnoSetup --exact
'@
}

if (-not $SkipPublish) {
    Write-Host "正在发布 SuperCV $Version（Windows x64）..."

    $artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
    $resolvedPublishDir = [System.IO.Path]::GetFullPath($publishDir)
    $requiredPrefix = $artifactsRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

    if (-not $resolvedPublishDir.StartsWith(
        $requiredPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝清理 artifacts 之外的发布目录：$resolvedPublishDir"
    }

    if (Test-Path -LiteralPath $resolvedPublishDir) {
        Remove-Item -LiteralPath $resolvedPublishDir -Recurse -Force
    }

    dotnet publish $projectPath `
        --configuration Release `
        --self-contained false `
        --output $publishDir `
        -p:Version=$Version `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:PublishTrimmed=false

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败，退出代码：$LASTEXITCODE"
    }
}

$publishedExe = Join-Path $publishDir 'SuperCV.exe'
if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf)) {
    throw "未找到发布后的程序：$publishedExe"
}

New-Item -ItemType Directory -Path $installerOutputDir -Force | Out-Null
$isccPath = Find-InnoSetupCompiler

if ($BundleRuntime) {
    $runtimeUrl = "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/$RuntimeVersion/windowsdesktop-runtime-$RuntimeVersion-win-x64.exe"
    $runtimeDirectory = Split-Path -Parent $runtimeInstaller
    New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null

    $runtimeHashMatches = $false
    if (Test-Path -LiteralPath $runtimeInstaller -PathType Leaf) {
        $existingRuntimeHash = Get-FileHash -LiteralPath $runtimeInstaller -Algorithm SHA256
        $runtimeHashMatches = $existingRuntimeHash.Hash -eq $RuntimeSha256
    }

    if (-not $runtimeHashMatches) {
        if (Test-Path -LiteralPath $runtimeInstaller -PathType Leaf) {
            Remove-Item -LiteralPath $runtimeInstaller -Force
        }

        Write-Host "正在下载用于完整安装包的 .NET Desktop Runtime $RuntimeVersion..."
        Start-BitsTransfer `
            -Source $runtimeUrl `
            -Destination $runtimeInstaller `
            -Priority Foreground `
            -DisplayName 'SuperCV complete installer runtime'
    }

    $downloadedRuntimeHash = Get-FileHash -LiteralPath $runtimeInstaller -Algorithm SHA256
    if ($downloadedRuntimeHash.Hash -ne $RuntimeSha256) {
        throw "运行时安装程序 SHA256 校验失败。预期：$RuntimeSha256；实际：$($downloadedRuntimeHash.Hash)"
    }
}

Write-Host '正在编译安装程序...'
$isccArguments = @(
    "/DMyAppVersion=$Version"
    "/DPublishDir=$publishDir"
)

if ($BundleRuntime) {
    $isccArguments += '/DMyAppPackageSuffix=-Full'
    $isccArguments += "/DBundledDotNetRuntimePath=$runtimeInstaller"
}

$isccArguments += $installerScript
& $isccPath $isccArguments

if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup 编译失败，退出代码：$LASTEXITCODE"
}

if (-not (Test-Path -LiteralPath $expectedInstaller -PathType Leaf)) {
    throw "安装程序编译完成，但未找到预期产物：$expectedInstaller"
}

$installerFile = Get-Item -LiteralPath $expectedInstaller
$hash = Get-FileHash -LiteralPath $expectedInstaller -Algorithm SHA256

Write-Host ''
Write-Host '安装包生成成功：'
[PSCustomObject]@{
    Path = $installerFile.FullName
    Version = $Version
    SizeMiB = [Math]::Round($installerFile.Length / 1MB, 2)
    SHA256 = $hash.Hash
}
