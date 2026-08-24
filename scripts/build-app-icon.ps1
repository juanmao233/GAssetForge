[CmdletBinding()]
param(
    [string]$SourcePath,
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$iconScriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$iconRepositoryRoot = (Resolve-Path (Join-Path $iconScriptDirectory "..")).Path
if ([string]::IsNullOrWhiteSpace($SourcePath)) {
    $SourcePath = Join-Path $iconRepositoryRoot "src\GAssetForge.App\Assets\Branding\GAssetForge.png"
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $iconRepositoryRoot "src\GAssetForge.App\Assets\Branding\GAssetForge.ico"
}

$iconSourcePath = [System.IO.Path]::GetFullPath($SourcePath)
$iconOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $iconSourcePath -PathType Leaf)) {
    throw "Icon source image does not exist: $iconSourcePath"
}

$iconOutputDirectory = Split-Path -Parent $iconOutputPath
New-Item -ItemType Directory -Path $iconOutputDirectory -Force | Out-Null

Add-Type -AssemblyName System.Drawing
$iconSizes = @(16, 24, 32, 48, 64, 128, 256)
$iconFrames = [System.Collections.Generic.List[byte[]]]::new()
$iconSourceImage = [System.Drawing.Image]::FromFile($iconSourcePath)
$iconSourceWidth = $iconSourceImage.Width
$iconSourceHeight = $iconSourceImage.Height
try {
    foreach ($iconSize in $iconSizes) {
        $iconBitmap = [System.Drawing.Bitmap]::new(
            $iconSize,
            $iconSize,
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $iconBitmap.SetResolution(96, 96)
            $iconGraphics = [System.Drawing.Graphics]::FromImage($iconBitmap)
            try {
                $iconGraphics.Clear([System.Drawing.Color]::Transparent)
                $iconGraphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $iconGraphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $iconGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $iconGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $iconGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $iconGraphics.DrawImage($iconSourceImage, 0, 0, $iconSize, $iconSize)
            }
            finally {
                $iconGraphics.Dispose()
            }

            $iconFrameStream = [System.IO.MemoryStream]::new()
            try {
                $iconBitmap.Save($iconFrameStream, [System.Drawing.Imaging.ImageFormat]::Png)
                $iconFrames.Add($iconFrameStream.ToArray())
            }
            finally {
                $iconFrameStream.Dispose()
            }
        }
        finally {
            $iconBitmap.Dispose()
        }
    }
}
finally {
    $iconSourceImage.Dispose()
}

$iconTemporaryPath = Join-Path $iconOutputDirectory ($([System.IO.Path]::GetFileName($iconOutputPath)) + "." + [Guid]::NewGuid().ToString("N") + ".tmp")
$iconStream = [System.IO.File]::Open(
    $iconTemporaryPath,
    [System.IO.FileMode]::CreateNew,
    [System.IO.FileAccess]::Write,
    [System.IO.FileShare]::None)
try {
    $iconWriter = [System.IO.BinaryWriter]::new($iconStream)
    try {
        $iconWriter.Write([UInt16]0)
        $iconWriter.Write([UInt16]1)
        $iconWriter.Write([UInt16]$iconFrames.Count)

        $iconDataOffset = 6 + 16 * $iconFrames.Count
        for ($iconIndex = 0; $iconIndex -lt $iconFrames.Count; $iconIndex++) {
            $iconSize = $iconSizes[$iconIndex]
            $iconFrame = $iconFrames[$iconIndex]
            $iconWriter.Write([byte]$(if ($iconSize -eq 256) { 0 } else { $iconSize }))
            $iconWriter.Write([byte]$(if ($iconSize -eq 256) { 0 } else { $iconSize }))
            $iconWriter.Write([byte]0)
            $iconWriter.Write([byte]0)
            $iconWriter.Write([UInt16]1)
            $iconWriter.Write([UInt16]32)
            $iconWriter.Write([UInt32]$iconFrame.Length)
            $iconWriter.Write([UInt32]$iconDataOffset)
            $iconDataOffset += $iconFrame.Length
        }

        foreach ($iconFrame in $iconFrames) {
            $iconWriter.Write($iconFrame)
        }
    }
    finally {
        $iconWriter.Dispose()
    }
}
catch {
    $iconStream.Dispose()
    Remove-Item -LiteralPath $iconTemporaryPath -Force -ErrorAction SilentlyContinue
    throw
}

[System.IO.File]::Move($iconTemporaryPath, $iconOutputPath, $true)

$iconVerification = [System.Drawing.Icon]::new($iconOutputPath, 256, 256)
try {
    Write-Host "Application icon created: $iconOutputPath"
    Write-Host "Source: $iconSourcePath ($($iconSourceWidth)x$($iconSourceHeight))"
    Write-Host "Frames: $($iconSizes -join ', ')"
}
finally {
    $iconVerification.Dispose()
}
