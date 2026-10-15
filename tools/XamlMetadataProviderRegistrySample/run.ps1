# Builds and runs the XamlMetadataProviderRegistry plugin sample against a Microsoft.WindowsAppSDK.WinUI package that
# has the API (3.0.0-dev from a WinUI build of this branch).
#
#   .\run.ps1 -WinUIPackageSource C:\hr-shared\winui\int-<sha>
#
# The package's version string never changes between WinUI builds, so restore always uses a fresh NUGET_PACKAGES.
# The host is launched inside the shared desktop lock when C:\hr-shared\DesktopLock.ps1 exists, and only the PID it
# started is ever stopped.

[CmdletBinding()]
param(
    [string]$WinUIPackageSource = "C:\mux-hr\PackageStore",
    [string]$OutDir = (Join-Path $env:TEMP "xmpr-sample"),
    [string]$NuGetFeed = "https://api.nuget.org/v3/index.json",
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

$env:NUGET_PACKAGES = Join-Path $OutDir ("packages-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$hostOut = Join-Path $OutDir "host"
$pluginOut = Join-Path $OutDir "plugin"
Remove-Item -Recurse -Force $hostOut, $pluginOut -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Force $OutDir | Out-Null
$config = Join-Path $OutDir "nuget.config"
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="winui" value="$WinUIPackageSource" />
    <add key="feed" value="$NuGetFeed" />
  </packageSources>
</configuration>
"@ | Set-Content $config
$sources = @("--configfile", $config)
$common = @("-c", "Release", "-p:Platform=x64", "-p:NuGetAudit=false", "--nologo", "-v:q")

Write-Output "### building PluginControls (WinUI package: $WinUIPackageSource) ###"
dotnet restore (Join-Path $here "PluginControls\PluginControls.csproj") @sources -p:Platform=x64 -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw "plugin restore failed" }
dotnet build (Join-Path $here "PluginControls\PluginControls.csproj") @common --no-restore -o $pluginOut
if ($LASTEXITCODE -ne 0) { throw "plugin build failed" }

Write-Output "### building PluginHost ###"
dotnet restore (Join-Path $here "Host\PluginHost.csproj") @sources -p:Platform=x64 -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw "host restore failed" }
dotnet build (Join-Path $here "Host\PluginHost.csproj") @common --no-restore -o $hostOut
if ($LASTEXITCODE -ne 0) { throw "host build failed" }

# The host has no reference to the plugin: it only finds it in its plugins folder at runtime.
$plugins = Join-Path $hostOut "plugins"
New-Item -ItemType Directory -Force $plugins | Out-Null
Copy-Item (Join-Path $pluginOut "PluginControls.dll") $plugins
if (Test-Path (Join-Path $hostOut "PluginControls.dll")) { throw "the host must not reference PluginControls.dll directly" }

$exe = Join-Path $hostOut "PluginHost.exe"
$results = Join-Path $hostOut "plugin-host-results.txt"
Remove-Item $results -ErrorAction SilentlyContinue

$script:exitCode = 1
$launch = {
    $p = Start-Process -FilePath $exe -WorkingDirectory $hostOut -PassThru
    $launchedPid = $p.Id
    Write-Output "LAUNCHED_PID: $launchedPid $exe"
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
        Write-Output "WATCHDOG: stopping PID $launchedPid"
        Stop-Process -Id $launchedPid -Force
    }
    Write-Output "EXITCODE: $($p.ExitCode)"
    $script:exitCode = $p.ExitCode
}

$lock = "C:\hr-shared\DesktopLock.ps1"
if (Test-Path $lock) {
    . $lock
    Use-HrDesktop -Owner "metadata-provider-api sample" $launch
} else {
    & $launch
}

Write-Output ""
Write-Output "### plugin-host-results.txt ###"
if (Test-Path $results) { Get-Content $results } else { Write-Output "(no results file)" }
exit $script:exitCode
