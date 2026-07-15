param(
    [string] $OutputPath = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) "start-widget.exe")
)

$ErrorActionPreference = "Stop"
$sourcePath = Join-Path $PSScriptRoot "Program.cs"
$iconPath = Join-Path $PSScriptRoot "icon.ico"
$buildScriptPath = $MyInvocation.MyCommand.Path
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($OutputPath)
if (![IO.Directory]::Exists($outputDirectory)) {
    throw "Launcher output directory does not exist: $outputDirectory"
}

$program = [IO.File]::ReadAllText($sourcePath).Replace("`r`n", "`n").Replace("`r", "`n")
$buildScript = [IO.File]::ReadAllText($buildScriptPath).Replace("`r`n", "`n").Replace("`r", "`n")
$icon = [Convert]::ToBase64String([IO.File]::ReadAllBytes($iconPath))
$manifest = $program + "`0" + $buildScript + "`0" + $icon
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $buildHash = [BitConverter]::ToString(
        $sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($manifest))
    ).Replace("-", "").ToLowerInvariant()
} finally {
    $sha256.Dispose()
}

$metadataPath = Join-Path ([IO.Path]::GetTempPath()) ("CodexUsageTray-LauncherMetadata-" + [guid]::NewGuid().ToString("N") + ".cs")
$metadata = "using System.Reflection;`r`n[assembly: AssemblyInformationalVersion(`"build-$buildHash`")]`r`n"
[IO.File]::WriteAllText($metadataPath, $metadata, (New-Object Text.UTF8Encoding($false)))
try {
    $csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    & $csc /nologo /target:winexe "/win32icon:$iconPath" "/out:$OutputPath" /reference:System.Windows.Forms.dll $sourcePath $metadataPath
    if ($LASTEXITCODE -ne 0) { throw "Launcher compilation failed with exit code $LASTEXITCODE." }
} finally {
    [IO.File]::Delete($metadataPath)
}
