# Windows version of pack-packages.sh.
# Builds the WebSvc.PayLink.* packages from packages/ into artifacts/packages, where PayLink/nuget.config finds them.
# A fresh build does this automatically; run this after changing anything in packages/ so PayLink picks up the change.
#   powershell -ExecutionPolicy Bypass -File scripts\pack-packages.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\packages'
New-Item -ItemType Directory -Force -Path $out | Out-Null

Get-ChildItem -Path (Join-Path $root 'packages') -Filter *.csproj -Recurse |
    Where-Object { $_.Name -notlike '*.Tests.csproj' } |
    ForEach-Object {
        dotnet pack $_.FullName -c Release -o $out --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed for $($_.Name)" }
    }

# Restore caches packages by version; drop cached copies so a rebuilt package with the same version is used.
$cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
Get-ChildItem -Path $cache -Directory -Filter 'websvc.paylink.*' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force

Write-Host "Packages written to $out:"
Get-ChildItem $out -Filter *.nupkg | ForEach-Object { Write-Host "  $($_.Name)" }
