[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$sourcePatterns = @(
    ".editorconfig",
    ".gitattributes",
    "*.cs",
    "*.csproj",
    "*.props",
    "*.targets",
    "*.xaml",
    "*.sln",
    "*.ps1"
)

$gitArguments = @("-C", $repositoryRoot, "ls-files", "--cached", "--others", "--exclude-standard", "--") + $sourcePatterns
$sourceFiles = @(& git @gitArguments | Sort-Object -Unique)
if ($LASTEXITCODE -ne 0)
{
    throw "git ls-files failed with exit code $LASTEXITCODE."
}

$issues = [Collections.Generic.List[string]]::new()
$strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
foreach ($relativePath in $sourceFiles)
{
    $fullPath = Join-Path $repositoryRoot $relativePath
    if (-not [IO.File]::Exists($fullPath))
    {
        $issues.Add("$relativePath`: listed source/build file is missing from the working tree.")
        continue
    }

    $bytes = [IO.File]::ReadAllBytes($fullPath)
    $hasUtf8Bom = $bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and
        $bytes[2] -eq 0xBF
    if ($hasUtf8Bom)
    {
        $issues.Add("$relativePath`: UTF-8 BOM is not allowed.")
    }

    try
    {
        [void]$strictUtf8.GetString($bytes)
    }
    catch [Text.DecoderFallbackException]
    {
        $issues.Add("$relativePath`: invalid UTF-8 byte sequence detected.")
    }

    if ([Array]::IndexOf($bytes, [byte]0x0D) -ge 0)
    {
        $issues.Add("$relativePath`: CR or CRLF detected; use LF line endings.")
    }

    if ($bytes.Length -gt 0 -and $bytes[$bytes.Length - 1] -ne 0x0A)
    {
        $issues.Add("$relativePath`: missing final LF newline.")
    }
}

if ($issues.Count -gt 0)
{
    foreach ($issue in $issues)
    {
        [Console]::Error.WriteLine($issue)
    }

    exit 1
}

Write-Host "Verified LF, UTF-8 without BOM, and final newlines for $($sourceFiles.Count) tracked or unignored source/build files."
