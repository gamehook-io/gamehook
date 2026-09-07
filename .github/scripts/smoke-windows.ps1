param(
    [Parameter(Mandatory = $true)][string]$Binary,
    [Parameter(Mandatory = $true)][string]$MapperPath
)

$ErrorActionPreference = 'Stop'
$env:GameHookProfileDirectory = Join-Path ([IO.Path]::GetTempPath()) ('gamehook-smoke-' + [guid]::NewGuid())
$env:MapperDirectory = [IO.Path]::GetFullPath($MapperPath)
$env:AppUpdateEnabled = 'false'
$application = Start-Process -FilePath (Resolve-Path $Binary).Path -PassThru
try {
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $application.Refresh()
        if ($application.HasExited) { throw 'GameHook exited before rendering a window.' }
        if ($application.MainWindowHandle -ne 0) { break }
        Start-Sleep -Milliseconds 500
    }
    if ($application.MainWindowHandle -eq 0) { throw 'GameHook did not render within 30 seconds.' }
    Start-Sleep -Seconds 10
    $application.Refresh()
    if ($application.HasExited) { throw 'GameHook exited during the startup soak.' }
    if (-not $application.CloseMainWindow()) { throw 'GameHook did not accept a window close request.' }
    if (-not $application.WaitForExit(10000)) { throw 'GameHook did not shut down within 10 seconds.' }
    if ($application.ExitCode -ne 0) { throw "GameHook exited with code $($application.ExitCode)." }
    Write-Output 'GameHook rendered, survived startup, and shut down cleanly.'
}
finally {
    if (-not $application.HasExited) { $application.Kill() }
    $application.Dispose()
}
