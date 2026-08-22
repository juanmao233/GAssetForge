[CmdletBinding()]
param(
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$publishScriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$publishRepositoryRoot = (Resolve-Path (Join-Path $publishScriptDirectory "..")).Path
$publishProjectPath = Join-Path $publishRepositoryRoot "src\GAssetForge.App\GAssetForge.App.csproj"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $publishRepositoryRoot "src\GAssetForge.App\bin\Release\net8.0-windows\win-x64\publish-single-file"
}

$publishOutputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $publishOutputPath -Force | Out-Null

Write-Host "Publishing GAssetForge for Windows x64..."
dotnet publish $publishProjectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --property:PublishProfile=Windows-x64 `
    --property:NuGetLockFilePath=obj\packages.publish-win-x64.lock.json `
    --property:RestoreForceEvaluate=true `
    --output $publishOutputPath

if ($LASTEXITCODE -ne 0) {
    throw "GAssetForge publish failed with exit code $LASTEXITCODE."
}

$publishExecutablePath = Join-Path $publishOutputPath "GAssetForge.exe"
if (-not (Test-Path -LiteralPath $publishExecutablePath -PathType Leaf)) {
    throw "Publish completed without the expected executable: $publishExecutablePath"
}

$publishExecutable = Get-Item -LiteralPath $publishExecutablePath
Copy-Item -LiteralPath (Join-Path $publishRepositoryRoot "THIRD-PARTY-NOTICES.md") `
    -Destination (Join-Path $publishOutputPath "THIRD-PARTY-NOTICES.md") `
    -Force
$publishLicenseDirectory = Join-Path $publishOutputPath "licenses"
New-Item -ItemType Directory -Path $publishLicenseDirectory -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $publishRepositoryRoot "licenses") -File |
    Copy-Item -Destination $publishLicenseDirectory -Force
$publishChecksum = Get-FileHash -Algorithm SHA256 -LiteralPath $publishExecutablePath
"$($publishChecksum.Hash.ToLowerInvariant())  $($publishExecutable.Name)" |
    Set-Content -LiteralPath (Join-Path $publishOutputPath "SHA256SUMS.txt") -Encoding ascii
Write-Host "Publish succeeded."
Write-Host "Executable: $($publishExecutable.FullName)"
Write-Host "Size: $([Math]::Round($publishExecutable.Length / 1MB, 2)) MB"
