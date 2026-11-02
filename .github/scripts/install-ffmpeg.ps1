<#
  Provisions the FFmpeg build that releases bundle, for CI test runs.

  The download URL and SHA-256 are read from publish.ps1, so CI always tests against exactly the FFmpeg that ships.
  The archive is cached in %LOCALAPPDATA%\ClementineBuild\downloads (the same place publish.ps1 uses), verified,
  unpacked with the 7-Zip installed on the machine, and its bin folder is added to PATH for later workflow steps.
  This script never runs the downloaded programs itself.

    powershell -NoProfile -ExecutionPolicy Bypass -File .github\scripts\install-ffmpeg.ps1 [-Destination <dir>] [-DownloadDirectory <dir>]
#>
param(
    [string] $Destination = $(if ($env:RUNNER_TEMP) { Join-Path $env:RUNNER_TEMP 'ffmpeg' } else { Join-Path $env:TEMP 'ClementineCiFfmpeg' }),
    [string] $DownloadDirectory = (Join-Path $env:LOCALAPPDATA 'ClementineBuild\downloads')
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$publishScript = [IO.File]::ReadAllText((Join-Path $repoRoot 'publish.ps1'))

# Url = '...ffmpeg-<version>-....7z'  (optional comment lines)  Sha256 = '<64 hex>'
$pin = [regex]::Match($publishScript,
    "Url\s*=\s*'(?<url>https://[^']+/(?<name>ffmpeg-[^'/]+\.7z))'\s*(?:#[^\r\n]*\s*)*Sha256\s*=\s*'(?<sha>[0-9A-Fa-f]{64})'")
if (-not $pin.Success) { throw "Couldn't find the pinned FFmpeg download (Url + Sha256) in publish.ps1." }
$url = $pin.Groups['url'].Value
$name = $pin.Groups['name'].Value
$expected = $pin.Groups['sha'].Value.ToUpperInvariant()

New-Item -ItemType Directory -Force $DownloadDirectory | Out-Null
$archive = Join-Path $DownloadDirectory $name
if ((Test-Path $archive) -and (Get-FileHash $archive -Algorithm SHA256).Hash -eq $expected) {
    Write-Host "Using cached $name"
} else {
    Write-Host "Downloading $url"
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing
}
$actual = (Get-FileHash $archive -Algorithm SHA256).Hash
if ($actual -ne $expected) {
    Remove-Item $archive -Force
    throw "$name has SHA-256 $actual, expected $expected. Refusing to use it."
}
Write-Host "SHA-256 verified: $expected"

$sevenZip = @("$env:ProgramFiles\7-Zip\7z.exe", "${env:ProgramFiles(x86)}\7-Zip\7z.exe", (Get-Command 7z.exe -ErrorAction SilentlyContinue).Source) |
    Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $sevenZip) { throw 'Unpacking FFmpeg needs 7-Zip (7z.exe) installed.' }

if (Test-Path $Destination) { Remove-Item $Destination -Recurse -Force }
& $sevenZip x $archive "-o$Destination" -y | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Unpacking $name failed (7-Zip exit code $LASTEXITCODE)." }

$ffmpeg = Get-ChildItem $Destination -Recurse -Filter ffmpeg.exe | Select-Object -First 1
if (-not $ffmpeg -or -not (Test-Path (Join-Path $ffmpeg.DirectoryName 'ffprobe.exe'))) {
    throw "The FFmpeg package has no bin folder with ffmpeg.exe and ffprobe.exe."
}
$bin = $ffmpeg.DirectoryName

if ($env:GITHUB_PATH) {
    # UTF-8 without BOM, one path per line (Windows PowerShell's >> would write UTF-16).
    [IO.File]::AppendAllText($env:GITHUB_PATH, "$bin`n")
    Write-Host "Added $bin to PATH for the next steps."
} else {
    Write-Host "FFmpeg is in $bin (not running in GitHub Actions, so PATH is unchanged)."
}
