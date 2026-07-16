param(
    [switch] $SkipBuild
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $PSScriptRoot "CodexUsageTray.csproj"
$testProject = Join-Path $repositoryRoot "tests\native\CodexUsageTray.NativeTests.csproj"
$launcher = Join-Path $repositoryRoot "Codex Tracker.exe"
$tempDirectory = Join-Path ([IO.Path]::GetTempPath()) ("CodexUsageTray-Check-" + [guid]::NewGuid().ToString("N"))
if (![IO.File]::Exists($launcher)) { throw "Codex Tracker.exe is missing." }
$signature = [IO.File]::ReadAllBytes($launcher)
if ($signature.Length -lt 2 -or $signature[0] -ne 0x4d -or $signature[1] -ne 0x5a) {
    throw "Codex Tracker.exe is not a Windows executable."
}

try {
    [IO.Directory]::CreateDirectory($tempDirectory) | Out-Null
    & dotnet restore $projectPath
    if ($LASTEXITCODE -ne 0) { throw "Native application restore failed." }
    if (!$SkipBuild) {
        & dotnet build $projectPath --configuration Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw "Native application build failed." }
    }
    & dotnet run --project $testProject --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "Native unit tests failed." }

    $sourceLauncher = Join-Path $tempDirectory "source-Codex Tracker.exe"
    & (Join-Path $PSScriptRoot "launcher\build.ps1") -OutputPath $sourceLauncher

    $responses = @()
    foreach ($candidate in @($launcher, $sourceLauncher)) {
        $token = [guid]::NewGuid().ToString("N")
        $ready = Join-Path $tempDirectory ("self-test-" + $token + ".ready")
        $process = Start-Process -FilePath $candidate `
            -ArgumentList "--self-test", "3", $ready, $token `
            -WorkingDirectory $repositoryRoot -Wait -PassThru
        if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $ready)) {
            throw "$([IO.Path]::GetFileName($candidate)) failed native protocol self-test."
        }
        $response = [IO.File]::ReadAllText($ready).Trim()
        [IO.File]::Delete($ready)
        if (!$response.StartsWith($token + "|") -or $response.Length -ne ($token.Length + 65)) {
            throw "$([IO.Path]::GetFileName($candidate)) returned invalid self-test data."
        }
        $responses += $response.Substring($token.Length + 1)
    }
    if ($responses[0] -ne $responses[1]) {
        throw "The tracked Codex Tracker.exe does not match the native build inputs."
    }
    $smokeToken = [guid]::NewGuid().ToString("N")
    $smokeReady = Join-Path $tempDirectory ("native-smoke-" + $smokeToken + ".ready")
    $smokeProcess = Start-Process -FilePath $launcher `
        -ArgumentList "--native-smoke-test", $smokeReady, $smokeToken `
        -WorkingDirectory $repositoryRoot -Wait -PassThru
    if ($smokeProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath $smokeReady) -or
        [IO.File]::ReadAllText($smokeReady).Trim() -ne $smokeToken) {
        throw "The tracked native application failed its WPF readiness smoke test."
    }
    [IO.File]::Delete($smokeReady)
} finally {
    if ([IO.Directory]::Exists($tempDirectory)) {
        [IO.Directory]::Delete($tempDirectory, $true)
    }
}

"Native source and launcher checks passed."
