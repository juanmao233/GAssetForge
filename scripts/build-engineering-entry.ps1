[CmdletBinding()]
param(
    [switch]$Verify
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$entryScriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$entryRepositoryRoot = (Resolve-Path (Join-Path $entryScriptDirectory "..")).Path
$entryProjectPath = Join-Path $entryRepositoryRoot `
    "src\GAssetForge.EngineeringLauncher\GAssetForge.EngineeringLauncher.csproj"
$entryPublishDirectory = Join-Path $entryRepositoryRoot "artifacts\engineering-launcher\publish"
$entryPublishedExecutable = Join-Path $entryPublishDirectory "GAssetForge.EngineeringLauncher.exe"
$entryRootExecutable = Join-Path $entryRepositoryRoot "GAssetForge.exe"
$entryTemporaryExecutable = Join-Path $entryRepositoryRoot ".GAssetForge.exe.installing"

if (-not (Test-Path -LiteralPath $entryProjectPath -PathType Leaf)) {
    throw "Engineering launcher project is missing: $entryProjectPath"
}

New-Item -ItemType Directory -Path $entryPublishDirectory -Force | Out-Null
Write-Host "Building GAssetForge engineering entry..."
dotnet publish $entryProjectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    --property:PublishSingleFile=true `
    --property:UseAppHost=true `
    --property:NuGetLockFilePath=obj\packages.publish-engineering-win-x64.lock.json `
    --property:RestoreForceEvaluate=true `
    --property:DebugSymbols=false `
    --property:DebugType=None `
    --output $entryPublishDirectory

if ($LASTEXITCODE -ne 0) {
    throw "Engineering launcher publish failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $entryPublishedExecutable -PathType Leaf)) {
    throw "Engineering launcher publish did not create: $entryPublishedExecutable"
}

$entryResolvedRoot = [System.IO.Path]::GetFullPath($entryRepositoryRoot)
$entryResolvedTarget = [System.IO.Path]::GetFullPath($entryRootExecutable)
if (-not [System.IO.Path]::GetDirectoryName($entryResolvedTarget).Equals(
        $entryResolvedRoot,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to install engineering entry outside repository root: $entryResolvedTarget"
}

Copy-Item -LiteralPath $entryPublishedExecutable -Destination $entryTemporaryExecutable -Force
Move-Item -LiteralPath $entryTemporaryExecutable -Destination $entryRootExecutable -Force

if ($Verify) {
    $entryVerification = Start-Process `
        -FilePath $entryRootExecutable `
        -ArgumentList "--verify-only" `
        -WorkingDirectory $entryRepositoryRoot `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    if ($entryVerification.ExitCode -ne 0) {
        throw "Engineering entry verification failed with exit code $($entryVerification.ExitCode)."
    }
}

$entryHash = Get-FileHash -Algorithm SHA256 -LiteralPath $entryRootExecutable
$entryFile = Get-Item -LiteralPath $entryRootExecutable
Write-Host "Engineering entry ready: $($entryFile.FullName)"
Write-Host "Size: $($entryFile.Length) bytes"
Write-Host "SHA256: $($entryHash.Hash.ToLowerInvariant())"
