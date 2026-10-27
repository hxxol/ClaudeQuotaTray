param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
function Invoke-DotNet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet command failed: $($Arguments -join ' ')" }
}
if (-not $SkipTests) {
    Invoke-DotNet @('run', '--project', "$root/tests/CodexQuotaTray.Tests.csproj", '-c', $Configuration, '--', '--fixtures-only')
    if ($Configuration -eq 'Release') { & "$root/tests/compare-legacy.ps1" }
}
foreach ($mode in @('self-contained', 'framework-dependent')) {
    $selfContained = if ($mode -eq 'self-contained') { 'true' } else { 'false' }
    $output = Join-Path $root "dist/win-x64-$mode"
    New-Item -ItemType Directory -Force $output | Out-Null
    Invoke-DotNet @('publish', "$root/CodexQuotaTray.csproj", '-c', $Configuration, '-r', 'win-x64',
        '--self-contained', $selfContained, '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        "-p:EnableCompressionInSingleFile=$selfContained", '-p:PublishTrimmed=false', '-p:DebugType=embedded', '-o', $output)
    $exe = Join-Path $output 'CodexQuotaTray.exe'
    if (-not (Test-Path $exe)) { throw "Missing expected single executable: $exe" }
    $unexpected = Get-ChildItem $output -File | Where-Object Name -ne 'CodexQuotaTray.exe'
    if ($unexpected) { throw "Publish emitted unexpected companion files: $($unexpected.Name -join ', ')" }
    $process = Start-Process -FilePath $exe -ArgumentList '--smoke-test' -PassThru
    if (-not $process.WaitForExit(30000)) { $process.Kill(); throw "Smoke test timed out: $mode" }
    if ($process.ExitCode -ne 0) { throw "Smoke test failed: $mode ($($process.ExitCode))" }
    (Get-FileHash $exe -Algorithm SHA256).Hash | Set-Content "$output.sha256" -Encoding ascii
}
Write-Host 'Both single-EXE distributions are available under dist/.'
