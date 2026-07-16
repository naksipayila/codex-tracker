param(
    [string] $OutputPath = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) "Codex Tracker.exe")
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectPath = Join-Path (Split-Path -Parent $PSScriptRoot) "CodexUsageTray.csproj"
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($OutputPath)
if (![IO.Directory]::Exists($outputDirectory)) {
    throw "Launcher output directory does not exist: $outputDirectory"
}

$buildInputs = @(
    "global.json",
    "src/CodexUsageTray.csproj",
    "src/app.manifest",
    "src/NativeApplication.cs",
    "src/NativeSettings.cs",
    "src/NativeMethods.cs",
    "src/WidgetWindow.cs",
    "src/NativeTypes.cs",
    "src/LatrixIntegration.cs",
    "src/UpdateService.cs",
    "src/launcher/Program.cs",
    "src/launcher/build.ps1",
    "src/launcher/icon.ico"
)
$manifest = New-Object Text.StringBuilder
foreach ($relativePath in $buildInputs) {
    $path = Join-Path $repositoryRoot ($relativePath.Replace("/", [IO.Path]::DirectorySeparatorChar))
    if (![IO.File]::Exists($path)) { throw "Native build input is missing: $relativePath" }
    [void] $manifest.Append($relativePath).Append("`0")
    if ($relativePath.EndsWith(".ico", [StringComparison]::OrdinalIgnoreCase)) {
        [void] $manifest.Append([Convert]::ToBase64String([IO.File]::ReadAllBytes($path)))
    } else {
        [void] $manifest.Append([IO.File]::ReadAllText($path).Replace("`r`n", "`n").Replace("`r", "`n"))
    }
    [void] $manifest.Append("`0")
}
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $buildHash = [BitConverter]::ToString(
        $sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($manifest.ToString()))
    ).Replace("-", "").ToLowerInvariant()
} finally {
    $sha256.Dispose()
}

$publishDirectory = Join-Path ([IO.Path]::GetTempPath()) ("CodexUsageTray-Publish-" + [guid]::NewGuid().ToString("N"))
try {
    Push-Location -LiteralPath $repositoryRoot
    try {
        & dotnet publish $projectPath `
            --configuration Release `
            --runtime win-x64 `
            --self-contained false `
            --output $publishDirectory `
            -p:PublishSingleFile=true `
            -p:InformationalVersion="build-$buildHash" `
            -p:DebugType=None `
            -p:DebugSymbols=false
        $publishExitCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }
    if ($publishExitCode -ne 0) { throw "Native application compilation failed with exit code $publishExitCode." }
    $publishedExecutable = Join-Path $publishDirectory "Codex Tracker.exe"
    if (![IO.File]::Exists($publishedExecutable)) { throw "The native publish did not produce Codex Tracker.exe." }
    [IO.File]::Copy($publishedExecutable, $OutputPath, $true)
} finally {
    if ([IO.Directory]::Exists($publishDirectory)) {
        [IO.Directory]::Delete($publishDirectory, $true)
    }
}
