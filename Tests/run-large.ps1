param(
    [Parameter(Mandatory=$true)][string]$DatasetPath,
    [int]$Rows = 6000000,
    [switch]$Stress
)
$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$dataset = [IO.Path]::GetFullPath($DatasetPath)
$outputFolder = Join-Path $workspaceRoot ('artifacts\performance-gib\' + (Split-Path -Leaf $dataset))
New-Item -ItemType Directory -Path $outputFolder -Force | Out-Null
$memory = Get-CimInstance Win32_OperatingSystem
$available = [int64]$memory.FreePhysicalMemory * 1024
if ($available -lt 6GB) { throw '시험 시작에 필요한 가용 RAM 6GiB가 없습니다.' }
$exe = Join-Path $PSScriptRoot 'bin\Release\Tests.exe'
$arguments = @('--ui-large', ('"' + $dataset + '"'), $Rows)
if ($Stress) { $arguments += '--stress' }
$outputFile = Join-Path $outputFolder 'stdout.txt'
$errorFile = Join-Path $outputFolder 'stderr.txt'
$monitorFile = Join-Path $outputFolder 'memory-monitor.csv'
Set-Content -LiteralPath $monitorFile -Value 'elapsed_seconds,private_bytes,working_set,peak_working_set,available_bytes' -Encoding utf8
$testProcess = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $workspaceRoot -NoNewWindow -RedirectStandardOutput $outputFile -RedirectStandardError $errorFile -PassThru
$clock = [Diagnostics.Stopwatch]::StartNew()
$failure = ''
$previousReport = -1
while (-not $testProcess.HasExited) {
    Start-Sleep -Seconds 2
    $testProcess.Refresh()
    if ($testProcess.HasExited) { break }
    $available = [int64](Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory * 1024
    $elapsed = [int]$clock.Elapsed.TotalSeconds
    Add-Content -LiteralPath $monitorFile -Value "$elapsed,$($testProcess.PrivateMemorySize64),$($testProcess.WorkingSet64),$($testProcess.PeakWorkingSet64),$available" -Encoding utf8
    if ([int]($elapsed / 10) -ne $previousReport) {
        $previousReport = [int]($elapsed / 10)
        Write-Output ('{0}s private={1:N2}GiB free={2:N2}GiB' -f $elapsed,($testProcess.PrivateMemorySize64/1GB),($available/1GB))
    }
    if ($available -lt 1536MB) { $failure = '가용 RAM 1.5GiB 미만으로 시험 프로세스만 중단' }
    elseif ($testProcess.PrivateMemorySize64 -gt 9GB) { $failure = '시험 프로세스 private memory 9GiB 초과로 중단' }
    elseif ($clock.Elapsed.TotalSeconds -gt 900) { $failure = '시험 제한 시간 900초 초과로 중단' }
    if ($failure) {
        # 이 스크립트가 시작한 테스트 프로세스만 종료하며 사용자 분석기는 건드리지 않는다.
        Stop-Process -Id $testProcess.Id -Force
        $testProcess.WaitForExit()
        break
    }
}
$testProcess.WaitForExit()
Get-Content -LiteralPath $outputFile
Get-Content -LiteralPath $errorFile
if ($failure) {
    Set-Content -LiteralPath (Join-Path $outputFolder 'resource-stop.txt') -Value $failure -Encoding utf8
    throw $failure
}
if ($testProcess.ExitCode -ne 0) { throw ('대용량 시험 실패, 종료 코드: ' + $testProcess.ExitCode) }
