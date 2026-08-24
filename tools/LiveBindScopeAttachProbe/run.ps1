$appDir = "C:\Users\nikolame\.copilot\session-state\f2399b3c-cd73-4391-b669-a6a367b9a891\files\probe\Probe\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64"
$scopeDll = "C:\Users\nikolame\.copilot\session-state\f2399b3c-cd73-4391-b669-a6a367b9a891\files\probe\ProbeScope\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\ProbeScope.dll"
Copy-Item $scopeDll -Destination $appDir -Force
Remove-Item (Join-Path $appDir "probe-results.txt") -ErrorAction SilentlyContinue
$exe = Join-Path $appDir "LiveBindScopeProbe.exe"
Write-Output "EXE: $exe"
$p = Start-Process -FilePath $exe -WorkingDirectory $appDir -PassThru -RedirectStandardOutput (Join-Path $appDir "stdout.txt") -RedirectStandardError (Join-Path $appDir "stderr.txt")
$pid_owned = $p.Id
Write-Output "LAUNCHED_PID: $pid_owned"
Write-Output "LAUNCHED_PATH: $exe"
$exited = $p.WaitForExit(120000)
if (-not $exited) {
    Write-Output "WATCHDOG: terminating owned exact PID $pid_owned"
    Stop-Process -Id $pid_owned -Force
    Start-Sleep -Seconds 2
}
Write-Output "EXITED: $($p.HasExited) EXITCODE: $($p.ExitCode)"
Write-Output "PID_STILL_RUNNING: $([bool](Get-Process -Id $pid_owned -ErrorAction SilentlyContinue))"
