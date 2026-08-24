# Portable build+run of the bindscope component scenarios.
#
# Compiles the real production source (..\XamlBindScopeAttachCore.cpp) and the real shared scenario
# suite with cl.exe only. No init.cmd, no NuGet restore, no product build, no XAML headers.
#
#   .\run-portable.ps1            clean suite only
#   .\run-portable.ps1 -Mutants   clean suite plus every seeded mutant
#
# The component source includes "precomp.h", which in a product build is the shared XAML precompiled
# header. Nothing in this component needs it, so a trivial stub is generated into the scratch output
# directory and put first on the include path.
#
# __XAML_UNITTESTS__ is defined here for the same reason components\unittest.props defines it: it is
# what makes the seeded mutants exist. The shipping library is compiled without it.

[CmdletBinding()]
param(
    [switch]$Mutants,
    [string]$OutDir = (Join-Path $env:TEMP "bindscope-portable")
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere not found; a Visual Studio C++ toolset is required." }

$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { throw "No Visual Studio installation with the C++ toolset was found." }

$vcvars = Join-Path $vsPath "VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found at $vcvars" }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# Stub for the product precompiled header this component does not actually need.
@"
// Generated stub. The bindscope component has no XAML dependencies, so the product precompiled
// header is not required to compile it outside the product build.
#pragma once
"@ | Set-Content (Join-Path $OutDir "precomp.h")

$exe = Join-Path $OutDir "BindScopePortableRunner.exe"
$sources = @(
    (Join-Path $here "BindScopePortableRunner.cpp")
    (Join-Path $here "..\XamlBindScopeAttachCore.cpp")
) | ForEach-Object { '"' + (Resolve-Path $_).Path + '"' }

$includes = @(
    (Resolve-Path (Join-Path $here "..\inc")).Path
    $OutDir
    (Resolve-Path $here).Path
) | ForEach-Object { '/I"' + $_ + '"' }

$clArgs = "/nologo /std:c++17 /EHsc /W4 /WX /permissive- /D_UNICODE /DUNICODE /D__XAML_UNITTESTS__ " +
          ($includes -join " ") + " " + ($sources -join " ") +
          " /Fe:`"$exe`" /Fo:`"$OutDir\\`""

Write-Output "### compiling production component source with cl.exe ###"
& $env:ComSpec /c "call `"$vcvars`" >nul && cl $clArgs"
if ($LASTEXITCODE -ne 0) { throw "compile failed with exit code $LASTEXITCODE" }

Write-Output "binary: $exe"
Write-Output "built:  $((Get-Item $exe).LastWriteTime.ToString('o'))"
Write-Output ""

if ($Mutants) { & $exe --mutants } else { & $exe }
$runExit = $LASTEXITCODE
Write-Output ""
Write-Output "exit=$runExit"
exit $runExit
