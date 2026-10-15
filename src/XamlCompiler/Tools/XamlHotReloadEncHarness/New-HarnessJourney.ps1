# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
.SYNOPSIS
  Turns a winapp watch-hot-reload journey (test\benchmarks\watch-hot-reload\journeys\*.json in winapp) into an app and a
  journey for XamlHotReloadEncHarness.

.DESCRIPTION
  Applies the journey's edits with winapp's own WatchJourney.psm1, up to and including -BaseThrough (typically the last
  project-file edit, such as a PackageReference, which the harness can't replay). That state is the harness's baseline:
  a git repository with a .gitignore for obj/bin, and the csc.args recording in Directory.Build.targets. Every later
  edit becomes one harness step that writes the whole file ('create-file'). Edits the harness can't replay (deleting a
  file, a project file) are skipped and listed.

  Then build the app once (dotnet build, with -p:XamlCompilerExePath=<the compiler under test>) and run the harness:
    XamlHotReloadEncHarness <Out>\app\<project> <Out>\journey.json --compiler <XamlCompiler.exe> --verify-build

.EXAMPLE
  .\New-HarnessJourney.ps1 -WinappBenchmarks C:\src\winapp\test\benchmarks\watch-hot-reload -Journey todo-app-extended `
      -Variant code-first -BaseThrough a12-add-mvvm-package -StopAfter x16-app-resource -Out C:\tmp\tae `
      -RunFolder C:\hr-real\runs\u22\20261015-091807\code-first
#>
param(
    [Parameter(Mandatory)][string]$WinappBenchmarks,
    [Parameter(Mandatory)][string]$Journey,
    [Parameter(Mandatory)][string]$Variant,
    [Parameter(Mandatory)][string]$BaseThrough,
    [string]$StopAfter,
    [Parameter(Mandatory)][string]$Out,
    # A pipeline run folder to copy Directory.Build.props/targets and nuget.config from (the WinUI package under test).
    [Parameter(Mandatory)][string]$RunFolder
)
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $WinappBenchmarks 'WatchJourney.psm1') -Force
$definition = Get-Content (Join-Path $WinappBenchmarks "journeys\$Journey.json") -Raw | ConvertFrom-Json
$plan = Get-JourneyPlan -Journey $definition -VariantId $Variant

if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
$app = Join-Path $Out 'app'
Copy-Item (Join-Path $WinappBenchmarks $definition.app.template) $app -Recurse
foreach ($file in 'Directory.Build.props', 'Directory.Build.targets', 'nuget.config') {
    Copy-Item (Join-Path $RunFolder $file) $Out
}
$targets = Join-Path $Out 'Directory.Build.targets'
$recordArgs = "  <PropertyGroup><ProvideCommandLineArgs>true</ProvideCommandLineArgs></PropertyGroup>`r`n" +
    "  <Target Name=`"WriteCscArgs`" AfterTargets=`"CoreCompile`"><WriteLinesToFile File=`"`$(IntermediateOutputPath)csc.args`" Lines=`"@(CscCommandLineArgs)`" Overwrite=`"true`" /></Target>`r`n</Project>"
[IO.File]::WriteAllText($targets, [IO.File]::ReadAllText($targets).Replace('</Project>', $recordArgs))

$index = 0
foreach ($step in $plan) {
    Invoke-JourneyEdit -AppDir $app -Action $step.Action
    $index++
    if ($step.Action.id -eq $BaseThrough) { break }
}
if ($index -eq $plan.Count -and $plan[$index - 1].Action.id -ne $BaseThrough) { throw "No action '$BaseThrough' in the plan." }
Set-Content (Join-Path $app '.gitignore') "obj/`nbin/"
Push-Location $app
try {
    git init -q
    git config core.autocrlf false
    git add -A
    git -c user.name=harness -c user.email=harness@localhost commit -qm baseline
}
finally { Pop-Location }

# The later edits, applied to a scratch copy to get each file's text after the save.
$scratch = Join-Path $Out 'scratch'
robocopy $app $scratch /E /XD .git /NFL /NDL /NJH /NJS /NP | Out-Null
$steps = [System.Collections.Generic.List[object]]::new()
foreach ($step in $plan[$index..($plan.Count - 1)]) {
    $action = $step.Action
    $parts = if ($action.edits) { @($action.edits) } else { @($action) }
    if ($parts | Where-Object { $_.edit.op -eq 'delete-file' -or $_.file -like '*.*proj' }) {
        Write-Host "skipped $($action.id) ($(($parts | ForEach-Object { $_.edit.op }) -join ', '))"
    }
    else {
        Invoke-JourneyEdit -AppDir $scratch -Action $action
        $n = 0
        foreach ($part in $parts) {
            $text = [IO.File]::ReadAllText((Join-Path $scratch $part.file)).TrimEnd("`n") -split "`n"
            $steps.Add([ordered]@{ id = $action.id + $(if ($n) { "-$n" } else { '' }); title = $action.id; file = $part.file; edit = [ordered]@{ op = 'create-file'; text = $text } })
            $n++
        }
    }
    if ($StopAfter -and $action.id -eq $StopAfter) { break }
}
Remove-Item $scratch -Recurse -Force
@{ segments = @(@{ actions = $steps }) } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $Out 'journey.json')
Write-Host "$($steps.Count) steps in $(Join-Path $Out 'journey.json'); baseline after '$BaseThrough' in $app"
