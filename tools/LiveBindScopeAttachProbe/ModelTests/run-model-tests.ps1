# Static gate driver: clean run plus one fresh process per mutation arm.
# No runtime app, no window, no Windows App SDK. Nothing here needs a network restore.

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $here "ModelTests.csproj"
$exe  = Join-Path $here "bin\Release\net8.0\ScopeAttachModelTests.exe"

Write-Output "### rebuild (clean) ###"
dotnet clean $proj -c Release -v:q --nologo | Out-Null
dotnet build $proj -c Release -v:q --nologo
if ($LASTEXITCODE -ne 0) { throw "build failed" }
Write-Output "binary: $exe"
Write-Output "built:  $((Get-Item $exe).LastWriteTime.ToString('o'))"

Write-Output ""
Write-Output "### clean run ###"
& $exe
$cleanExit = $LASTEXITCODE
Write-Output "cleanExit=$cleanExit (0 = all guards hold)"

$mutants = (& $exe --list-mutants)
$survivors = @()

foreach ($m in $mutants) {
    Write-Output ""
    Write-Output "### mutant $m (fresh process) ###"
    $out = & $exe --mutant $m
    $code = $LASTEXITCODE
    $out | Select-String -Pattern "^(S|X)\d+\s+FAIL|MUTANT|total=" | ForEach-Object { $_.Line }
    Write-Output "exit=$code"
    if ($code -ne 0) { $survivors += $m }
}

Write-Output ""
Write-Output "======================================================"
Write-Output "clean run exit : $cleanExit"
Write-Output "mutants        : $($mutants.Count)"
Write-Output "survivors      : $(if ($survivors.Count -eq 0) { '<none>' } else { $survivors -join ', ' })"
if ($cleanExit -eq 0 -and $survivors.Count -eq 0) {
    Write-Output "STATIC GATE: GREEN"
    exit 0
}
Write-Output "STATIC GATE: RED"
exit 1
