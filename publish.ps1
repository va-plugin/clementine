<#
  Builds the Clementine installer into ..\Clementine Product.

    .\publish.ps1                    version from Directory.Build.props
    .\publish.ps1 -Version 0.2.0

  Clementine Product\Windows\Clementine-Setup.exe     per-user installer (no admin): shortcuts, uninstall entry
  Clementine Product\Windows\Clementine-Portable.zip  unzip-and-run copy
  Clementine Product\VA\Clementine <version> - VA\    VA Plugin Store build (same installer) + release note

  The app is published self-contained (no .NET install needed) and packed with Velopack (vpk, a local dotnet tool
  pinned in dotnet-tools.json). The Velopack release feed (needed later for delta updates) is kept in
  releases\ next to this script (git-ignored).

  Bundled so users install nothing else (downloaded once into %LOCALAPPDATA%\ClementineBuild\downloads and
  checked against pinned SHA-256 hashes):
    tools\ffmpeg\bin\   FFmpeg 8.1.2 "full_build-shared" by Gyan Doshi (GPLv3; licence + source notes in licenses\)
    tools\7zip\7za.exe  7-Zip 25.01 standalone console version by Igor Pavlov (LGPL; licence in licenses\)
  Building needs 7-Zip installed on the build machine (to unpack those .7z packages; GitHub's Windows runners have it).

  From bash or another shell:  powershell -NoProfile -ExecutionPolicy Bypass -File publish.ps1
#>
param([string] $Version)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$build = Join-Path $root 'build.ps1'
# House layout (like the other VA products): Clementine\Clementine Product\Windows holds the Setup/portable for
# GitHub and direct sharing; Clementine\Clementine Product\VA\Clementine <version> - VA holds the VA Plugin Store build.
$productRoot = Join-Path (Split-Path $root -Parent) 'Clementine Product'
$product = Join-Path $productRoot 'Windows'
$publishDir = Join-Path $env:LOCALAPPDATA 'ClementineBuild\publish\Clementine'
$releases = Join-Path $root 'releases'
$icon = Join-Path $root 'src\Clementine.App\assets\clementine.ico'
$splash = Join-Path $root 'src\Clementine.App\assets\clementine-256.png'

if (-not $Version) {
    $props = [xml][IO.File]::ReadAllText((Join-Path $root 'Directory.Build.props'))
    $Version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
}
Write-Host "Publishing Clementine $Version" -ForegroundColor Cyan

# 1. Self-contained Release build of the app.
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
& $build dotnet publish (Join-Path $root 'src\Clementine.App') -c Release -r win-x64 --self-contained true `
    -p:DebugType=none -p:DebugSymbols=false "-p:Version=$Version" -o $publishDir -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

# Native packages ship debug symbols as "native assets" (libSkiaSharp.pdb alone is ~85 MB): drop every .pdb.
Get-ChildItem $publishDir -Recurse -Filter *.pdb | Remove-Item -Force
if (-not (Test-Path (Join-Path $publishDir 'Clementine.exe'))) { throw 'Clementine.exe is missing from the publish output.' }

# Bundle FFmpeg and 7-Zip so the installed app needs nothing else.
$bundles = @(
    @{
        Name = 'ffmpeg-8.1.2-full_build-shared.7z'
        Url = 'https://github.com/GyanD/codexffmpeg/releases/download/8.1.2/ffmpeg-8.1.2-full_build-shared.7z'
        # Matches https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-full_build-shared.7z.sha256
        Sha256 = 'CBA748035C21CE1431D0823C7A3A711F38616F89F87A265DCEDDF9B7F6749D2D'
    },
    @{
        Name = '7z2501-extra.7z'
        Url = 'https://github.com/ip7z/7zip/releases/download/25.01/7z2501-extra.7z'
        Sha256 = 'CD3CF38085C2CC6839CF72716DAFB3175AE425F4FD34FAAFC6C0B64D618D307F'
    }
)
$downloads = Join-Path $env:LOCALAPPDATA 'ClementineBuild\downloads'
New-Item -ItemType Directory -Force $downloads | Out-Null
$sevenZip = @("$env:ProgramFiles\7-Zip\7z.exe", "${env:ProgramFiles(x86)}\7-Zip\7z.exe", (Get-Command 7z.exe -ErrorAction SilentlyContinue).Source) |
    Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $sevenZip) { throw 'Building the installer needs 7-Zip installed (to unpack the FFmpeg and 7-Zip packages): winget install 7zip.7zip' }

$unpacked = @{}
foreach ($bundle in $bundles) {
    $file = Join-Path $downloads $bundle.Name
    if (-not (Test-Path $file) -or (Get-FileHash $file -Algorithm SHA256).Hash -ne $bundle.Sha256) {
        Write-Host "Downloading $($bundle.Name)"
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $bundle.Url -OutFile $file -UseBasicParsing
    }
    $hash = (Get-FileHash $file -Algorithm SHA256).Hash
    if ($hash -ne $bundle.Sha256) { throw "$($bundle.Name) has SHA-256 $hash, expected $($bundle.Sha256). Refusing to bundle it." }
    $target = Join-Path $downloads ([IO.Path]::GetFileNameWithoutExtension($bundle.Name))
    if (-not (Test-Path $target)) {
        & $sevenZip x $file "-o$target" -y | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Unpacking $($bundle.Name) failed ($LASTEXITCODE)." }
    }
    $unpacked[$bundle.Name] = $target
}

$licenses = Join-Path $publishDir 'licenses'
New-Item -ItemType Directory -Force $licenses | Out-Null

$ffmpegRoot = Get-ChildItem $unpacked['ffmpeg-8.1.2-full_build-shared.7z'] -Directory | Select-Object -First 1
$ffmpegBin = Join-Path $publishDir 'tools\ffmpeg\bin'
New-Item -ItemType Directory -Force $ffmpegBin | Out-Null
Get-ChildItem (Join-Path $ffmpegRoot.FullName 'bin') -File | Where-Object Name -ne 'ffplay.exe' | Copy-Item -Destination $ffmpegBin
Copy-Item (Join-Path $ffmpegRoot.FullName 'LICENSE') (Join-Path $licenses 'FFmpeg-LICENSE.txt')
Copy-Item (Join-Path $ffmpegRoot.FullName 'README.txt') (Join-Path $licenses 'FFmpeg-BUILD-README.txt')
[IO.File]::WriteAllText((Join-Path $licenses 'FFmpeg-SOURCE.txt'), @"
Clementine bundles FFmpeg 8.1.2 ("full_build-shared" by Gyan Doshi) as separate programs in tools\ffmpeg\bin.
FFmpeg is licensed under the GNU General Public License version 3 for this build (see FFmpeg-LICENSE.txt).
FFmpeg-BUILD-README.txt lists the build configuration, the external libraries compiled into its DLLs and, under
"external libraries' versions", the version of each library.

Corresponding source:
  FFmpeg 8.1.2:        ffmpeg-8.1.2.tar.xz, attached to every Clementine release on GitHub
                       (https://github.com/va-plugin/clementine/releases); also
                       https://github.com/FFmpeg/FFmpeg/tree/n8.1.2
  External libraries:  each library's own project, at the version FFmpeg-BUILD-README.txt lists
  Build:               https://www.gyan.dev/ffmpeg/builds/  (package ffmpeg-8.1.2-full_build-shared)
  Binary package:      https://github.com/GyanD/codexffmpeg/releases/tag/8.1.2

The source code of 7-Zip 25.01 (7z2501-src.7z), the other bundled program, is attached to the same releases.

Written offer:
  For at least three years after the last Clementine release that includes this FFmpeg build, Visual Artform will
  give anyone who asks a complete copy of its Corresponding Source, as the GNU General Public License version 3
  defines it (FFmpeg and every external library in the build, at the versions listed in FFmpeg-BUILD-README.txt),
  free of charge as a download. To ask, open an issue titled "FFmpeg source request" at
  https://github.com/va-plugin/clementine/issues
"@, (New-Object Text.UTF8Encoding($false)))

$sevenZipDir = Join-Path $publishDir 'tools\7zip'
New-Item -ItemType Directory -Force $sevenZipDir | Out-Null
Copy-Item (Join-Path $unpacked['7z2501-extra.7z'] 'x64\7za.exe') $sevenZipDir
Copy-Item (Join-Path $unpacked['7z2501-extra.7z'] 'License.txt') (Join-Path $licenses '7-Zip-License.txt')
Write-Host 'Bundled FFmpeg 8.1.2 and 7-Zip 25.01'
$sizeMb = [math]::Round(((Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB)
Write-Host "Published $sizeMb MB to $publishDir"

# 2. Pack with Velopack. vpk targets an older .NET; let it run on the installed .NET 10.
$env:DOTNET_ROLL_FORWARD = 'Major'
New-Item -ItemType Directory -Force $releases | Out-Null
& $build dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed ($LASTEXITCODE)." }
# packId "ClementineApp": Velopack installs to %LOCALAPPDATA%\<packId>, which must not be the app's own data folder
# (%LOCALAPPDATA%\Clementine holds logs and the LibreOffice profile).
& $build dotnet vpk pack `
    --packId ClementineApp --packVersion $Version --packTitle Clementine --packAuthors 'Visual Artform' `
    --packDir $publishDir --mainExe Clementine.exe --icon $icon --splashImage $splash --outputDir $releases
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)." }

# 3. Copy the two user-facing files into Clementine Product\Windows.
New-Item -ItemType Directory -Force $product | Out-Null
$setup = Get-ChildItem $releases -Filter '*-Setup.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$portable = Get-ChildItem $releases -Filter '*-Portable.zip' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $setup) { throw 'vpk produced no Setup.exe.' }
Copy-Item $setup.FullName (Join-Path $product 'Clementine-Setup.exe') -Force
if ($portable) { Copy-Item $portable.FullName (Join-Path $product 'Clementine-Portable.zip') -Force }
# The Release workflow attaches these two files: tell it where they are rather than have it repeat the layout above.
if ($env:GITHUB_OUTPUT) {
    # UTF-8 without BOM (Windows PowerShell's >> would write UTF-16).
    [IO.File]::AppendAllText($env:GITHUB_OUTPUT, "product_dir=$product`n")
}

# 4. The VA Plugin Store build: the same installer, named and noted like the other VA products, so
#    "va plugin Windows builds\Update Windows builds.bat" and the store's upload-builds tool pick it up.
$vaName = "Clementine $Version - VA"
$vaDir = Join-Path $productRoot "VA\$vaName"
New-Item -ItemType Directory -Force $vaDir | Out-Null
$storeFile = Join-Path $vaDir "$vaName Plugin Store installer (win-x64).exe"
Copy-Item $setup.FullName $storeFile -Force
$storeSize = (Get-Item $storeFile).Length
$storeHash = (Get-FileHash $storeFile -Algorithm SHA256).Hash.ToLowerInvariant()
$today = Get-Date -Format 'yyyy-MM-dd'
[IO.File]::WriteAllText((Join-Path $vaDir 'RELEASE BUILD - free, no licence key.txt'), @"
RELEASE BUILD - free, no licence key

Clementine $Version, VA Plugin Store edition for Windows (win-x64): a free standalone app (not a plugin),
installed by its own per-user installer. Free and open source (MIT): no licence check, no watermark.

Built $today from Clementine\Build with publish.ps1: self-contained .NET, with FFmpeg and 7-Zip bundled, so
customers install nothing else. Installs per user (no admin) to %LOCALAPPDATA%\ClementineApp, adds Start-menu and
desktop shortcuts and an Installed apps entry; uninstalling removes everything it registered.
The store file is byte-identical to Clementine Product\Windows\Clementine-Setup.exe.

Not code-signed yet: on first run Windows SmartScreen shows "Windows protected your PC"
(More info -> Run anyway).

Store build: $vaName Plugin Store installer (win-x64).exe
  Install as installer, platform win-x64, free
  $($storeSize.ToString('N0', [Globalization.CultureInfo]::InvariantCulture)) bytes, SHA-256 $storeHash

BUILDS READY line:
Clementine | $Version (free, no licence) | $storeFile | win-x64 | installer | $($storeSize.ToString('N0', [Globalization.CultureInfo]::InvariantCulture)) bytes | $storeHash
"@, (New-Object Text.UTF8Encoding($false)))

Write-Host ''
Write-Host "Done. Installer: $(Join-Path $product 'Clementine-Setup.exe')" -ForegroundColor Green
Write-Host "VA Plugin Store build: $storeFile" -ForegroundColor Green
Get-ChildItem $product, $vaDir | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
