<#
.SYNOPSIS
  Installs decoded item artwork into the bundled icon pack and pins it in Data/Icons/manifest.json.

.DESCRIPTION
  The icon pack's pinned mirror (josh-dastmalchi/exiledata-assets @ 3354944d) ships item art as WebP, and
  this machine has no WebP codec (WPF/WIC refuses the file). Decoding is therefore a one-off step outside
  the solution — the same arrangement docs/VALIDATION.md describes for the BC7 tree atlases:

      mirror 2DItems/**.webp
        → decoded with SixLabors.ImageSharp 3.1.5 in a throwaway net10.0 console project
        → PNG, max 256 px for jewels / 128 px for the 417 unique sprites, into this script's -Source folder

  This script is the second half: it re-encodes each PNG at the shipped size, copies it under
  Data/Icons/Items/2DItems/… (the layout ItemArt.RelativePath produces for "Art/2DItems/…dds"), and adds
  every file to manifest.json's filesSha256 with the new fileCount. Nothing is invented: a sprite that the
  mirror does not carry (13 as of this run) simply stays unbundled and the app falls back to the art of the
  item's base type.

.EXAMPLE
  pwsh build/extract-icon-art.ps1 -Source C:\work\pb-jewels -Size 256
  pwsh build/extract-icon-art.ps1 -Source C:\work\pb-uniques -Size 128
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [string]$Destination,
    [int]$Size = 256,
    [string]$MirrorCommit = '3354944d293daaf518cc0a9a60e3bb771f0ec21d',
    [string]$MirrorRepo = 'https://github.com/josh-dastmalchi/exiledata-assets'
)

Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
if (-not $Destination) { $Destination = Join-Path $root 'src\PoeBuilder.App\Data\Icons' }
if (-not (Test-Path $Source)) { throw "source folder not found: $Source" }
$manifestPath = Join-Path $Destination 'manifest.json'
if (-not (Test-Path $manifestPath)) { throw "icon manifest not found: $manifestPath" }

function Save-Resized([string]$In, [string]$Out, [int]$Max) {
    $image = New-Object System.Drawing.Bitmap($In)
    $scale = [Math]::Min(1.0, $Max / [Math]::Max($image.Width, $image.Height))
    $width = [int][Math]::Max(1, [Math]::Round($image.Width * $scale))
    $height = [int][Math]::Max(1, [Math]::Round($image.Height * $scale))
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.DrawImage($image, 0, 0, $width, $height)
    $graphics.Dispose()
    $bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose(); $image.Dispose()
}

$rows = @()
foreach ($file in (Get-ChildItem $Source -Recurse -Filter '*.png' | Sort-Object FullName)) {
    $relative = $file.FullName.Substring($Source.Length).TrimStart('\', '/') -replace '\\', '/'
    $target = Join-Path $Destination ($relative -replace '/', '\')
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Save-Resized $file.FullName $target $Size
    $rows += [pscustomobject]@{
        RelativePath = $relative
        KB = [Math]::Round((Get-Item $target).Length / 1KB)
        Sha256 = (Get-FileHash -Path $target -Algorithm SHA256).Hash.ToLower()
    }
}

# manifest.json is rebuilt in its own key order: the bundled file list is what IconService reads, so every
# installed sprite must appear there with its pinned hash.
$manifest = Get-Content -Raw $manifestPath | ConvertFrom-Json
$files = [ordered]@{}
foreach ($property in $manifest.filesSha256.PSObject.Properties) { $files[$property.Name] = $property.Value }
foreach ($row in $rows) { $files[$row.RelativePath] = $row.Sha256 }
$gemIcons = [ordered]@{}
foreach ($property in $manifest.gemIcons.PSObject.Properties) { $gemIcons[$property.Name] = $property.Value }
$classFallback = [ordered]@{}
foreach ($property in $manifest.classFallback.PSObject.Properties) { $classFallback[$property.Name] = $property.Value }
$sources = New-Object System.Collections.ArrayList
foreach ($entry in @($manifest.sources)) {
    # Only real objects are carried over: a manifest written by a damaged run can hold strings here, and
    # re-serializing those would keep the damage instead of repairing it.
    if ($entry -isnot [System.Management.Automation.PSCustomObject]) { continue }
    if (-not $entry.PSObject.Properties['usedFor']) { continue }
    [void]$sources.Add([ordered]@{ repo = [string]$entry.repo; commit = [string]$entry.commit; usedFor = [string]$entry.usedFor })
}
if (-not ($sources | Where-Object { $_.usedFor -like '*jewel*' })) {
    [void]$sources.Add([ordered]@{
        repo = $MirrorRepo
        commit = $MirrorCommit
        usedFor = 'jewel bases, jewel uniques and the unique item sprites the pack lacked (Art/2DItems webp mirror), decoded to PNG outside the solution (no WebP codec on the build machine)'
    })
}
$updated = [ordered]@{
    sources = @($sources)
    convertedTo = $manifest.convertedTo
    gamePatchEquivalenceVerified = $manifest.gamePatchEquivalenceVerified
    copyright = $manifest.copyright
    fileCount = $files.Count
    filesSha256 = $files
    gemIcons = $gemIcons
    classFallback = $classFallback
}
$text = ($updated | ConvertTo-Json -Depth 4 -Compress)
[System.IO.File]::WriteAllText($manifestPath, $text, (New-Object System.Text.UTF8Encoding($false)))

$rows | Format-Table RelativePath, KB -AutoSize
"installed: $($rows.Count) file(s), $((($rows | Measure-Object KB -Sum).Sum / 1024).ToString('0.0')) MB"
"manifest fileCount: $($files.Count)"
"manifest sha256: " + (Get-FileHash -Path $manifestPath -Algorithm SHA256).Hash.ToLower()
