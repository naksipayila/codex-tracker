param(
    [string] $OutputPath = (Join-Path (Split-Path -Parent $PSScriptRoot) "dist\Codex Tracker Setup.exe"),
    [string] $Version = $env:CODEX_VERSION,
    [string] $PortableOutputPath = ""
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($OutputPath)
$issPath = Join-Path $PSScriptRoot "CodexTracker.iss"

if ([string]::IsNullOrWhiteSpace($Version)) { $Version = "1.0.0" }
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Installer version must use numeric major.minor.patch format: $Version"
}
if (![IO.Directory]::Exists($outputDirectory)) {
    [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}
if (![string]::IsNullOrWhiteSpace($PortableOutputPath)) {
    $PortableOutputPath = [IO.Path]::GetFullPath($PortableOutputPath)
    $portableDirectory = [IO.Path]::GetDirectoryName($PortableOutputPath)
    if (![IO.Directory]::Exists($portableDirectory)) {
        [IO.Directory]::CreateDirectory($portableDirectory) | Out-Null
    }
}
if (![IO.File]::Exists($issPath)) { throw "Installer script is missing: $issPath" }

$innoCandidates = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
) | Where-Object { ![string]::IsNullOrWhiteSpace($_) -and [IO.File]::Exists($_) }
$isccPath = $innoCandidates | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($isccPath)) {
    throw "Inno Setup Compiler (ISCC.exe) was not found. Install Inno Setup 6 or set PATH."
}

$publishDirectory = Join-Path ([IO.Path]::GetTempPath()) ("CodexUsageTray-Installer-" + [guid]::NewGuid().ToString("N"))
[IO.Directory]::CreateDirectory($publishDirectory) | Out-Null
try {
    $nativeBuild = Join-Path $repositoryRoot "src\launcher\build.ps1"
    & $nativeBuild -OutputPath (Join-Path $publishDirectory "Codex Tracker.exe") -SelfContained -Version $Version
    if ($LASTEXITCODE -ne 0) { throw "Self-contained native publish failed with exit code $LASTEXITCODE." }
    if (![string]::IsNullOrWhiteSpace($PortableOutputPath)) {
        [IO.File]::Copy((Join-Path $publishDirectory "Codex Tracker.exe"), $PortableOutputPath, $true)
    }

    $arguments = @(
        "/Qp",
        "/DAppVersion=$Version",
        "/DPublishDir=$publishDirectory",
        "/DOutputDir=$outputDirectory",
        $issPath
    )
    & $isccPath @arguments
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE." }
    if (![IO.File]::Exists($OutputPath)) { throw "The installer was not produced: $OutputPath" }

    $hash = (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $hashPath = $OutputPath + ".sha256"
    [IO.File]::WriteAllText(
        $hashPath,
        $hash + " *" + [IO.Path]::GetFileName($OutputPath) + [Environment]::NewLine,
        (New-Object Text.UTF8Encoding($false))
    )
    $metadata = [ordered]@{
        version = $Version
        file = [IO.Path]::GetFileName($OutputPath)
        sha256 = $hash
    }
    [IO.File]::WriteAllText(
        ($OutputPath + ".json"),
        ($metadata | ConvertTo-Json),
        (New-Object Text.UTF8Encoding($false))
    )
    Write-Output "Installer: $OutputPath"
    Write-Output "SHA-256: $hash"
}
finally {
    if ([IO.Directory]::Exists($publishDirectory)) {
        [IO.Directory]::Delete($publishDirectory, $true)
    }
}
