<#
  Works out the release version from a pushed tag and sanity-checks Directory.Build.props before publishing.

    powershell -NoProfile -ExecutionPolicy Bypass -File .github\scripts\release-info.ps1 -Tag v0.2.0 -Repository <owner>/<repo>

  Tags look like v1.2.3 or v1.2.3-beta.1 (a suffix makes a pre-release). In GitHub Actions the results are written to
  $GITHUB_OUTPUT as version, prerelease (true/false) and repo_url.

  A stale <Version> or an empty <ClementineUpdateRepo> only becomes a workflow warning. A <ClementineUpdateRepo> that
  names a different repository stops the release: that value is the update feed installed copies are meant to trust,
  so it must point at the repository the release is actually published to.
#>
param(
    [Parameter(Mandatory = $true)] [string] $Tag,
    [Parameter(Mandatory = $true)] [string] $Repository,
    [string] $ServerUrl = 'https://github.com'
)

$ErrorActionPreference = 'Stop'

function Write-ReleaseWarning([string] $message) {
    if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host "::warning::$message" } else { Write-Warning $message }
}

if ($Tag -notmatch '^v(?<version>\d+\.\d+\.\d+(?<pre>-[0-9A-Za-z][0-9A-Za-z.-]*)?)$') {
    throw "Tag '$Tag' isn't a release tag. Use v<major>.<minor>.<patch>, optionally with a suffix: v1.2.3 or v1.2.3-beta.1."
}
$version = $Matches['version']
$prerelease = [bool]$Matches['pre']
$repoUrl = "$($ServerUrl.TrimEnd('/'))/$Repository"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$props = [xml][IO.File]::ReadAllText((Join-Path $repoRoot 'Directory.Build.props'))
$groups = @($props.Project.PropertyGroup)
$propsVersion = @($groups | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
$updateRepo = @($groups | ForEach-Object { $_.ClementineUpdateRepo } | Where-Object { $_ })[0]

if ($propsVersion -ne $version) {
    Write-ReleaseWarning "Directory.Build.props says <Version>$propsVersion</Version> but the tag is $Tag. The release is built as $version; bump <Version> so local builds match."
}
if (-not $updateRepo) {
    Write-ReleaseWarning "<ClementineUpdateRepo> in Directory.Build.props is empty. Set it to $repoUrl so installed copies can be pointed at this repository for updates."
} else {
    # GitHub owner and repository names are case-insensitive; -ne compares case-insensitively.
    $configured = ($updateRepo.Trim().TrimEnd('/')) -replace '\.git$', ''
    if ($configured -ne $repoUrl) {
        throw "<ClementineUpdateRepo> in Directory.Build.props is '$updateRepo', but this release is published to $repoUrl. Set it to $repoUrl, commit, then delete and push the tag again."
    }
}

$pre = if ($prerelease) { 'true' } else { 'false' }
Write-Host "Release $version (prerelease: $pre) for $repoUrl"
if ($env:GITHUB_OUTPUT) {
    # UTF-8 without BOM (Windows PowerShell's >> would write UTF-16).
    [IO.File]::AppendAllText($env:GITHUB_OUTPUT, "version=$version`nprerelease=$pre`nrepo_url=$repoUrl`n")
}
