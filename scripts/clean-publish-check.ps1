param([int]$Port = 8786)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'publish'
$node = (Get-Command node -ErrorAction Stop).Source
$savedPath = $env:PATH
$savedData = $env:PHOTOREEL_DATA
$savedRoot = $env:PHOTOREEL_ROOT
$savedPort = $env:PHOTOREEL_PORT
$app = $null
$report = @{ python_available = $false; dotnet_available = $false; browser = $false; process_restart = $false }
function Start-TestServer {
    $process = Start-Process -FilePath (Join-Path $output 'Photoreel.exe') -ArgumentList '--serve' -WorkingDirectory $output -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root 'evidence/clean-server.log') -RedirectStandardError (Join-Path $root 'evidence/clean-server-error.log')
    for ($i = 0; $i -lt 150; $i++) {
        if ($process.HasExited) { throw 'Published server exited before startup' }
        try { $null = Invoke-RestMethod "http://127.0.0.1:$Port/api/meta"; return $process } catch { Start-Sleep -Milliseconds 200 }
    }
    Stop-Process -Id $process.Id -Force
    throw 'Published server startup timeout'
}
Push-Location $root
try {
    $env:PATH = Join-Path $env:SystemRoot 'System32'
    $report.python_available = [bool](Get-Command python,python3 -ErrorAction SilentlyContinue)
    $report.dotnet_available = [bool](Get-Command dotnet -ErrorAction SilentlyContinue)
    if ($report.python_available -or $report.dotnet_available) { throw 'Clean PATH contains Python or dotnet' }
    $env:PHOTOREEL_ROOT = $output
    $env:PHOTOREEL_DATA = Join-Path $root 'evidence/clean-data'
    $env:PHOTOREEL_PORT = [string]$Port
    $env:PHOTOREEL_TEST_URL = "http://127.0.0.1:$Port"
    $app = Start-TestServer
    & $node (Join-Path $PSScriptRoot 'browser-checks.cjs')
    if ($LASTEXITCODE -ne 0) { throw 'Browser check failed' }
    $report.browser = $true
    $browser = Get-Content -LiteralPath 'evidence/browser-results.json' -Raw | ConvertFrom-Json
    $id = $browser.pid
    $before = Invoke-RestMethod "$env:PHOTOREEL_TEST_URL/api/projects/$id"
    $null = Invoke-RestMethod "$env:PHOTOREEL_TEST_URL/api/projects/$id/render" -Method Post
    for ($i = 0; $i -lt 150; $i++) {
        $running = Invoke-RestMethod "$env:PHOTOREEL_TEST_URL/api/projects/$id/render"
        if ($running.stage -like '*그리는 중*') { break }
        if ($running.state -ne 'running') { throw 'Render exited before process audit' }
        Start-Sleep -Milliseconds 100
    }
    if ($running.stage -notlike '*그리는 중*') { throw 'Renderer did not reach frame production' }
    $jobFile = Join-Path $env:PHOTOREEL_DATA "projects/$id/job.json"
    if ((Get-Content -LiteralPath $jobFile -Raw | ConvertFrom-Json).state -ne 'running') { throw 'Crash test did not start a real job' }
    $descendants = Get-CimInstance Win32_Process | Where-Object ParentProcessId -eq $app.Id
    $report.child_processes = @($descendants | ForEach-Object Name)
    if ($report.child_processes -match 'python') { throw 'Python child process found' }
    Stop-Process -Id $app.Id -Force
    $app.WaitForExit(); $app = $null
    foreach ($child in $descendants) { if ($child.Name -eq 'ffmpeg.exe') { Stop-Process -Id $child.ProcessId -Force -ErrorAction SilentlyContinue } }
    $app = Start-TestServer
    $restored = Invoke-RestMethod "$env:PHOTOREEL_TEST_URL/api/projects/$id"
    if ($restored.job.state -ne 'error' -or $restored.renders[-1].status -ne 'interrupted') { throw 'First crash restore response omitted interrupted history' }
    if ($restored.photos.Count -ne $before.photos.Count -or $restored.plan.style -ne $before.plan.style) { throw 'Project restore differs' }
    $report.process_restart = $true
    $report.history_count = $restored.renders.Count
    $report.application = 'Self-contained Windows x64 Photoreel.exe'
    $report.pass = $true
} finally {
    if ($app -and !$app.HasExited) { Stop-Process -Id $app.Id -Force }
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'evidence/clean-publish-results.json') -Encoding UTF8
    $env:PATH = $savedPath; $env:PHOTOREEL_DATA = $savedData; $env:PHOTOREEL_ROOT = $savedRoot; $env:PHOTOREEL_PORT = $savedPort
    Remove-Item Env:PHOTOREEL_TEST_URL -ErrorAction SilentlyContinue
    Pop-Location
}
$report | ConvertTo-Json -Depth 5
