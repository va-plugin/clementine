<#
  Fetches the source code of the FFmpeg and 7-Zip versions that publish.ps1 bundles, so every GitHub release carries
  it next to the installer. FFmpeg (GPLv3 build) and 7-Zip (LGPL) are redistributed inside Clementine, and their
  licences ask for the corresponding source to be offered from the same place as the programs.

    powershell -NoProfile -ExecutionPolicy Bypass -File .github\scripts\release-sources.ps1 [-OutputDirectory <dir>]

  The bundled versions are read from publish.ps1. When a version changes there, this script fails until the matching
  source archive and its SHA-256 are added to $sources below, so a release can't ship without its source.
  Archives are cached in %LOCALAPPDATA%\ClementineBuild\downloads (next to the packages publish.ps1 downloads),
  verified, and copied to the output directory (default: releases\sources, which is git-ignored).
  Nothing downloaded here is ever run.
#>
param(
    [string] $OutputDirectory,
    [string] $DownloadDirectory = (Join-Path $env:LOCALAPPDATA 'ClementineBuild\downloads')
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'releases\sources' }

# Source archives of the bundled versions, pinned like the binary packages in publish.ps1.
#   ffmpeg-<version>.tar.xz  FFmpeg's signed release tarball from ffmpeg.org. The 8.1.2 hash is the one Debian
#                            recorded for the same upstream file (ffmpeg_8.1.2.orig.tar.xz, 11,710,924 bytes).
#   7z<version>-src.7z       7-Zip's source package; the hash is the digest GitHub lists for the release asset.
$sources = @{
    'ffmpeg-8.1.2.tar.xz' = @{
        Url = 'https://ffmpeg.org/releases/ffmpeg-8.1.2.tar.xz'
        Sha256 = '464BEB5E7BF0C311E68B45AE2F04E9CC2AF88851ABB4082231742A74D97B524C'
    }
    '7z2501-src.7z' = @{
        Url = 'https://github.com/ip7z/7zip/releases/download/25.01/7z2501-src.7z'
        Sha256 = '2AED39B8F1238464475E9DE7DDA169A5E873A1DC8BBF4F664B943EABA5620181'
    }
}

# publish.ps1 pins e.g. .../ffmpeg-8.1.2-full_build-shared.7z and .../7z2501-extra.7z
$publishScript = [IO.File]::ReadAllText((Join-Path $repoRoot 'publish.ps1'))
$ffmpeg = [regex]::Match($publishScript, "/ffmpeg-(?<version>\d+(?:\.\d+)+)-[A-Za-z_-]+\.7z'")
$sevenZip = [regex]::Match($publishScript, "/7z(?<version>\d{4})-extra\.7z'")
if (-not $ffmpeg.Success) { throw "Couldn't find the bundled FFmpeg package URL in publish.ps1." }
if (-not $sevenZip.Success) { throw "Couldn't find the bundled 7-Zip package URL in publish.ps1." }
$needed = @("ffmpeg-$($ffmpeg.Groups['version'].Value).tar.xz", "7z$($sevenZip.Groups['version'].Value)-src.7z")

New-Item -ItemType Directory -Force $DownloadDirectory | Out-Null
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
foreach ($name in $needed) {
    $source = $sources[$name]
    if (-not $source) {
        throw "publish.ps1 bundles a version whose source isn't pinned yet: add '$name' (URL and SHA-256) to `$sources in .github\scripts\release-sources.ps1."
    }
    $file = Join-Path $DownloadDirectory $name
    if (-not (Test-Path $file) -or (Get-FileHash $file -Algorithm SHA256).Hash -ne $source.Sha256) {
        Write-Host "Downloading $($source.Url)"
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $source.Url -OutFile $file -UseBasicParsing
    }
    $hash = (Get-FileHash $file -Algorithm SHA256).Hash
    if ($hash -ne $source.Sha256) {
        Remove-Item $file -Force
        throw "$name has SHA-256 $hash, expected $($source.Sha256). Refusing to publish it."
    }
    Copy-Item $file (Join-Path $OutputDirectory $name) -Force
    Write-Host "SHA-256 verified: $name"
}
Write-Host "Source archives for the release are in $OutputDirectory"
