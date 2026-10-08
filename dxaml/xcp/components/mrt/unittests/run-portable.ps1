# Builds and runs the XAML hot reload registry tests with cl.exe alone: no init.cmd, no product build.
#
#   .\run-portable.ps1

[CmdletBinding()]
param(
    [string]$OutDir = (Join-Path $env:TEMP "xamlhotreload-portable")
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere not found; a Visual Studio C++ toolset is required." }
$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { throw "No Visual Studio installation with the C++ toolset was found." }
$vcvars = Join-Path $vsPath "VC\Auxiliary\Build\vcvars64.bat"

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$exe = Join-Path $OutDir "XamlHotReloadRegistryTests.exe"
$source = (Resolve-Path (Join-Path $here "XamlHotReloadRegistryTests.cpp")).Path
$include = (Resolve-Path (Join-Path $here "..\..\..\core\inc")).Path

$clArgs = "/nologo /std:c++17 /EHsc /W4 /WX /permissive- /D_UNICODE /DUNICODE /I`"$include`" `"$source`" /Fe:`"$exe`" /Fo:`"$OutDir\\`""
& $env:ComSpec /c "call `"$vcvars`" >nul && cl $clArgs"
if ($LASTEXITCODE -ne 0) { throw "compile failed with exit code $LASTEXITCODE" }

& $exe
exit $LASTEXITCODE
