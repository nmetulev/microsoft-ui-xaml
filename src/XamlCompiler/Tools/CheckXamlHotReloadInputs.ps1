# Checks that hot reload builds leave the compiler service's inputs (input.json) and other builds don't change.
#   VS MSBuild, Debug   (EnableXamlHotReload on, the compiler runs in-process): pass 2 input.json, EnableXamlHotReload=true
#   VS MSBuild, Release (in-process, hot reload off): no input.json
#   dotnet build, Debug (SDK-style C# projects; XamlCompiler.exe):  pass 2 input.json, EnableXamlHotReload=true
# -PropsAndTargets points the project at a compiler package layout (build\ and tools\) other than the one it restored.
param(
    [Parameter(Mandatory = $true)][string]$Project,
    [string]$PropsAndTargets,
    [string]$Platform = 'x64',
    [string]$MSBuild = 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\amd64\MSBuild.exe')

$ErrorActionPreference = 'Stop'
$projectDir = Split-Path (Resolve-Path $Project)
$isCpp = $Project.EndsWith('.vcxproj', [StringComparison]::OrdinalIgnoreCase)
$override = @(); if ($PropsAndTargets) { $override = @("/p:XamlCompilerPropsAndTargetsDirectory=$PropsAndTargets") }
$failures = 0

function Get-Inputs {
    Get-ChildItem $projectDir -Recurse -Filter input.json -File |
        Where-Object { $_.FullName -notmatch '\\packages\\' -and ($_.FullName -match '\\obj\\' -or $_.FullName -match '\\Generated Files\\') }
}

function Check([string]$label, [scriptblock]$build, [bool]$expected) {
    Get-Inputs | Remove-Item
    & $build 2>&1 | Select-String ': error' | Select-Object -First 5 | ForEach-Object { "  $_" }
    if ($LASTEXITCODE -ne 0) { "FAIL $label`: build failed ($LASTEXITCODE)"; $script:failures++; return }
    $inputs = @(Get-Inputs)
    if (-not $expected) {
        if ($inputs.Count -eq 0) { "PASS $label`: no input.json" } else { "FAIL $label`: wrote $($inputs.FullName -join ', ')"; $script:failures++ }
        return
    }
    if ($inputs.Count -ne 1) { "FAIL $label`: expected one input.json, found $($inputs.Count): $((Get-ChildItem $projectDir -Recurse -Filter input.json -File | ForEach-Object FullName) -join ', ')"; $script:failures++; return }
    $o = Get-Content $inputs[0].FullName -Raw | ConvertFrom-Json
    if ($o.IsPass1 -eq $false -and $o.CompileMode -eq 'RealBuildPass2' -and $o.EnableXamlHotReload -eq $true) {
        "PASS $label`: $($inputs[0].FullName.Substring($projectDir.Length + 1)) (pass 2, EnableXamlHotReload=true)"
    } else {
        "FAIL $label`: IsPass1=$($o.IsPass1) CompileMode=$($o.CompileMode) EnableXamlHotReload=$($o.EnableXamlHotReload)"; $script:failures++
    }
}

Push-Location $projectDir
try {
    $restore = @(); if (-not $isCpp) { $restore = @('/restore') }
    Check 'VS MSBuild, Debug' { & $MSBuild $Project @restore /p:Configuration=Debug /p:Platform=$Platform @override /m /nodeReuse:false /v:m } $true
    Check 'VS MSBuild, Release' { & $MSBuild $Project @restore /p:Configuration=Release /p:Platform=$Platform @override /m /nodeReuse:false /v:m } $false
    if (-not $isCpp) {
        $dotnetOverride = @($override | ForEach-Object { $_ -replace '^/p:', '-p:' })
        Check 'dotnet build, Debug' { dotnet build $Project -c Debug @dotnetOverride -v q -nologo } $true
    }
} finally {
    Pop-Location
}
"$failures failure(s)"
exit $failures
