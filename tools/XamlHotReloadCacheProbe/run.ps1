<#
.SYNOPSIS
    Builds and runs the XAML hot reload cache probe: one app process per scenario and mode.
.EXAMPLE
    .\run.ps1 -PackageSource C:\mux-hr\PackageStore
    .\run.ps1 -Scenarios page-reinit,merged-swap -Modes text
#>
param(
    # Folder holding Microsoft.WindowsAppSDK.WinUI.3.0.0-dev.nupkg (a WinUI build with IXamlHotReloadService).
    [string]$PackageSource = 'C:\mux-hr\PackageStore',
    [string[]]$Scenarios = @(
        'merged-new', 'merged-swap', 'merged-resetsource', 'negcache-swap', 'generic', 'page-new', 'page-reinit',
        'page-reinit-reset', 'page-reinit-broken', 'dict-reinit', 'loose', 'cycle', 'frame-cache', 'app-reload',
        'implicit-code-swap', 'resetsource-plain', 'deferral'),
    # text: XAML text through SetComponentMarkup; binary: the compiled .xbf; nodiag: no diagnostics attached (controls).
    [string[]]$Modes = @('text', 'binary'),
    [string]$OutDir = (Join-Path $PSScriptRoot "runs\$(Get-Date -Format 'yyyyMMdd-HHmmss')"),
    # Private package cache: the dev package's version never changes, so a shared cache would serve a stale build.
    [string]$NuGetPackages = (Join-Path $PSScriptRoot 'packages'),
    [switch]$SkipBuild,
    # Optional: a script block that serializes UI runs on a shared desktop (it receives the run as its argument).
    [scriptblock]$DesktopLock
)
$ErrorActionPreference = 'Stop'
$probe = Join-Path $PSScriptRoot 'probe'
$exe = Join-Path $probe 'bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\CacheProbe.exe'

if (-not $SkipBuild) {
    $vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath
    $tap = Join-Path $PSScriptRoot 'tap'
    & $env:ComSpec /c "call `"$vs\VC\Auxiliary\Build\vcvars64.bat`" >nul && cd /d `"$tap`" && cl /nologo /O2 /EHsc /LD ProbeTap.cpp /link /DEF:ProbeTap.def ole32.lib uuid.lib /OUT:ProbeTap.dll >nul"
    if ($LASTEXITCODE) { throw 'building ProbeTap.dll failed' }
    $env:NUGET_PACKAGES = $NuGetPackages
    dotnet build (Join-Path $probe 'CacheProbe.csproj') -c Debug -p:Platform=x64 -nologo -v q "-p:RestoreAdditionalProjectSources=$PackageSource"
    if ($LASTEXITCODE) { throw 'building the probe failed' }
}

New-Item -ItemType Directory $OutDir -Force | Out-Null
$run = {
    foreach ($mode in $Modes) {
        foreach ($s in $Scenarios) {
            $out = Join-Path $OutDir "$s.$mode.txt"
            $p = Start-Process $exe -ArgumentList $s, $mode, $out -PassThru
            if (-not $p.WaitForExit(60000)) { Stop-Process -Id $p.Id -Force; "SCENARIO $s $mode TIMEOUT" | Set-Content $out }
            if (-not (Test-Path $out)) { "SCENARIO $s $mode CRASH exit=$($p.ExitCode)" | Set-Content $out }
        }
    }
}
if ($DesktopLock) { & $DesktopLock $run } else { & $run }

Get-ChildItem $OutDir -Filter *.txt | Where-Object Name -ne 'all.log' | ForEach-Object { Get-Content $_.FullName } | Set-Content (Join-Path $OutDir 'all.log')
Write-Host "run folder: $OutDir"
Get-Content (Join-Path $OutDir 'all.log') | Select-String '^SCENARIO' | ForEach-Object { $_.Line }
