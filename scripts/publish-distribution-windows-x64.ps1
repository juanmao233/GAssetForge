[CmdletBinding()]
param(
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$distributionScriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$distributionRepositoryRoot = (Resolve-Path (Join-Path $distributionScriptDirectory "..")).Path
$desktopProject = Join-Path $distributionRepositoryRoot "src\GAssetForge.App\GAssetForge.App.csproj"
$mcpProject = Join-Path $distributionRepositoryRoot "src\GAssetForge.McpServer\GAssetForge.McpServer.csproj"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $distributionRepositoryRoot "dist\GAssetForge-win-x64"
}

$distributionOutputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $distributionOutputPath -Force | Out-Null

Write-Host "Publishing GAssetForge desktop executable..."
dotnet publish $desktopProject `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --property:PublishProfile=Windows-x64 `
    --output $distributionOutputPath

if ($LASTEXITCODE -ne 0) {
    throw "GAssetForge desktop publish failed with exit code $LASTEXITCODE."
}

Write-Host "Publishing GAssetForge MCP Server..."
dotnet publish $mcpProject `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --property:PublishProfile=win-x64 `
    --output $distributionOutputPath

if ($LASTEXITCODE -ne 0) {
    throw "GAssetForge MCP publish failed with exit code $LASTEXITCODE."
}

$desktopExecutable = Join-Path $distributionOutputPath "GAssetForge.exe"
$mcpExecutable = Join-Path $distributionOutputPath "GAssetForge.McpServer.exe"
foreach ($requiredExecutable in @($desktopExecutable, $mcpExecutable)) {
    if (-not (Test-Path -LiteralPath $requiredExecutable -PathType Leaf)) {
        throw "Distribution is missing executable: $requiredExecutable"
    }
}

Copy-Item -LiteralPath (Join-Path $distributionRepositoryRoot "docs\MCP.md") `
    -Destination (Join-Path $distributionOutputPath "MCP-README.md") `
    -Force
Copy-Item -LiteralPath (Join-Path $distributionRepositoryRoot "mcp-config.example.json") `
    -Destination (Join-Path $distributionOutputPath "mcp-config.example.json") `
    -Force
Copy-Item -LiteralPath (Join-Path $distributionRepositoryRoot "THIRD-PARTY-NOTICES.md") `
    -Destination (Join-Path $distributionOutputPath "THIRD-PARTY-NOTICES.md") `
    -Force

$distributionLicenseDirectory = Join-Path $distributionOutputPath "licenses"
New-Item -ItemType Directory -Path $distributionLicenseDirectory -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $distributionRepositoryRoot "licenses") -File |
    Copy-Item -Destination $distributionLicenseDirectory -Force

$checksums = Get-FileHash -Algorithm SHA256 -LiteralPath $desktopExecutable, $mcpExecutable
$checksumPath = Join-Path $distributionOutputPath "SHA256SUMS.txt"
$checksums |
    ForEach-Object { "$($_.Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($_.Path))" } |
    Set-Content -LiteralPath $checksumPath -Encoding ascii

Write-Host "Distribution succeeded: $distributionOutputPath"
Get-Item -LiteralPath $desktopExecutable, $mcpExecutable |
    Select-Object Name, Length, FullName |
    Format-Table -AutoSize
$checksums |
    Select-Object Path, Hash |
    Format-Table -AutoSize
