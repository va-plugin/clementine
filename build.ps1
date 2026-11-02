<#
  Clementine build helper.

  Every dotnet invocation runs under a machine-local mutex, so several terminals (or agents) never
  build at the same time and fight over obj/bin files. Uses the user-local .NET 10 SDK in
  %LOCALAPPDATA%\Microsoft\dotnet when present.

    .\build.ps1                                  build the whole solution (Debug)
    .\build.ps1 build <project-folder|csproj>    build one project (+ its references)
    .\build.ps1 test  <test-project> [args...]   dotnet test, e.g.  --filter "FullyQualifiedName~ProRes"
    .\build.ps1 cli   <cli args...>              build + run clementine-cli (list / run / tools)
    .\build.ps1 app   <app args...>              build + run Clementine.exe and wait for it to exit
    .\build.ps1 dotnet <raw dotnet args...>      any dotnet command, e.g.  dotnet add src\X package Foo

  From bash or another shell:  powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 <verb> ...
#>
# Deliberately no param() block: every argument lands in $args verbatim. Named parameters would swallow
# dotnet/CLI switches by prefix (-v:minimal → -Verb, --out → -OutVariable).
$Verb = if ($args.Count -gt 0) { [string]$args[0] } else { 'build' }
$Rest = @($args | Select-Object -Skip 1)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$artifacts = Join-Path $env:LOCALAPPDATA 'ClementineBuild'

$dotnetDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
$dotnet = Join-Path $dotnetDir 'dotnet.exe'
if (Test-Path $dotnet) {
    $env:DOTNET_ROOT = $dotnetDir
    $env:PATH = "$dotnetDir;$env:PATH"
} else {
    $dotnet = 'dotnet'
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:MSBUILDTERMINALLOGGER = 'off'

function Find-Exe([string] $project, [string] $exeName) {
    $exe = Get-ChildItem -Path (Join-Path $artifacts "bin\$project") -Filter $exeName -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $exe) { throw "Couldn't find $exeName under $artifacts\bin\$project - did the build succeed?" }
    return $exe.FullName
}

function Get-Solution {
    $sln = Get-ChildItem -Path $root -Filter 'Clementine.sln*' | Select-Object -First 1
    if (-not $sln) { throw 'Clementine solution file not found.' }
    return $sln.FullName
}

$mutex = [System.Threading.Mutex]::new($false, 'Local\ClementineBuildLock')
$acquired = $false
$code = 0
Push-Location $root
try {
    try { $acquired = $mutex.WaitOne([TimeSpan]::FromMinutes(30)) }
    catch [System.Threading.AbandonedMutexException] { $acquired = $true }
    if (-not $acquired) { throw 'Timed out waiting for the Clementine build lock (another build is still running).' }

    switch ($Verb) {
        'build' {
            $target = if ($Rest.Count -gt 0) { $Rest[0] } else { Get-Solution }
            $extra = @($Rest | Select-Object -Skip 1)
            & $dotnet build $target -nologo -v:minimal @extra
            $code = $LASTEXITCODE
        }
        'test' {
            $target = if ($Rest.Count -gt 0) { $Rest[0] } else { Get-Solution }
            $extra = @($Rest | Select-Object -Skip 1)
            # A hung test must never hold the shared build lock for long: abort the test host after 10 minutes.
            if (-not ($extra -match '^--blame-hang')) { $extra += @('--blame-hang-timeout', '10m') }
            & $dotnet test $target -nologo -v:minimal @extra
            $code = $LASTEXITCODE
        }
        'cli' {
            & $dotnet build (Join-Path $root 'src\Clementine.Cli') -nologo -v:quiet
            $code = $LASTEXITCODE
            if ($code -eq 0) {
                & (Find-Exe 'Clementine.Cli' 'clementine-cli.exe') @Rest
                $code = $LASTEXITCODE
            }
        }
        'app' {
            & $dotnet build (Join-Path $root 'src\Clementine.App') -nologo -v:quiet
            $code = $LASTEXITCODE
            if ($code -eq 0) {
                $p = Start-Process -FilePath (Find-Exe 'Clementine.App' 'Clementine.exe') -ArgumentList $Rest -PassThru -Wait
                $code = $p.ExitCode
            }
        }
        'dotnet' {
            & $dotnet @Rest
            $code = $LASTEXITCODE
        }
        default { throw "Unknown verb '$Verb'. Use build, test, cli, app or dotnet." }
    }
}
finally {
    Pop-Location
    if ($acquired) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
exit $code
