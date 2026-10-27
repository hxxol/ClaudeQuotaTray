$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'build/compatibility'
New-Item -ItemType Directory -Force $out | Out-Null
# Prove the parser's only source migration is its serializer namespace.
$old = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'legacy/SessionQuotaReader.cs')).Replace("`r`n", "`n")
$new = [IO.File]::ReadAllText((Join-Path $root 'src/SessionQuotaReader.cs')).Replace("`r`n", "`n").Replace('using CodexQuotaTray.Compatibility;', 'using System.Web.Script.Serialization;')
if ($old -cne $new) { throw 'Quota reader changed beyond its serializer namespace.' }
$csc = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $csc /nologo /codepage:65001 /define:LEGACY_ORACLE /target:exe /out:"$out/LegacyOracle.exe" /reference:System.Web.Extensions.dll /reference:System.Core.dll `
    "$PSScriptRoot/CompatibilityContract.cs" "$PSScriptRoot/legacy/SessionQuotaReader.cs" "$PSScriptRoot/legacy/Models.cs"
if ($LASTEXITCODE -ne 0) { throw 'Legacy oracle compilation failed.' }
& "$out/LegacyOracle.exe" "$out/legacy.txt"
if ($LASTEXITCODE -ne 0) { throw 'Legacy oracle failed.' }
& dotnet run --project "$PSScriptRoot/CodexQuotaTray.Tests.csproj" -c Release --no-build -- --compat "$out/modern.txt"
if ($LASTEXITCODE -ne 0) { throw 'Modern contract failed.' }
$difference = Compare-Object (Get-Content "$out/legacy.txt") (Get-Content "$out/modern.txt") -SyncWindow 0
if ($difference) { $difference | Format-Table | Out-String | Write-Host; throw 'Legacy and modern serializer/quota parser outputs differ.' }
Write-Host 'Original Framework and modern JSON/quota contract outputs match.'
