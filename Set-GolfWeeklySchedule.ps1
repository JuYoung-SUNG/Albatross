# "Albatross Golf Weekly Update" 작업을 등록한다.
#
# 대회는 대부분 일요일에 끝나므로 월요일 아침에 돌린다.
# 지난주 우승자와 갱신된 상금랭킹이 사이트에 반영된다.
#
# 관리자 권한이 필요할 수 있다. 실패하면 아래를 관리자 PowerShell에서 실행:
#   powershell -ExecutionPolicy Bypass -File .\Set-GolfWeeklySchedule.ps1
#
# 해제:  Unregister-ScheduledTask -TaskName "Albatross Golf Weekly Update" -Confirm:$false

$ErrorActionPreference = "Stop"

$taskName = "Albatross Golf Weekly Update"
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$script   = Join-Path $repoRoot "Run-GolfUpdate.ps1"

if (-not (Test-Path $script)) {
    Write-Host "Run-GolfUpdate.ps1 을 찾을 수 없습니다: $script" -ForegroundColor Red
    exit 1
}

# 이미 있으면 지우고 다시 만든다 (설정이 바뀌었을 수 있으므로)
try {
    Get-ScheduledTask -TaskName $taskName -ErrorAction Stop | Out-Null
    Write-Host "기존 작업을 제거하고 다시 등록합니다..." -ForegroundColor Yellow
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
} catch { }

$action = New-ScheduledTaskAction `
    -Execute "powershell.exe" `
    -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$script`"" `
    -WorkingDirectory $repoRoot

# 월요일 오전 8시
$trigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek Monday -At 8:00AM

# 로그온한 동안만 실행 -> 비밀번호 저장이 필요 없어 프롬프트가 뜨지 않는다
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive

# 컴퓨터가 꺼져 있어 놓친 경우 켜지면 실행. 오래 걸리면 2시간에서 끊는다.
$settings = New-ScheduledTaskSettingsSet `
    -StartWhenAvailable `
    -DontStopIfGoingOnBatteries `
    -AllowStartIfOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Hours 2)

Register-ScheduledTask -TaskName $taskName `
    -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
    -Description "KLPGA 대회·기록을 수집해 Albatross Golf 사이트를 갱신하고 배포합니다." | Out-Null

Write-Host "`n=== 등록 완료 ===" -ForegroundColor Cyan
$t = Get-ScheduledTask -TaskName $taskName
$i = Get-ScheduledTaskInfo -TaskName $taskName
[PSCustomObject]@{
    TaskName = $t.TaskName
    State    = $t.State
    NextRun  = $i.NextRunTime
} | Format-List

Write-Host "지금 바로 한 번 돌려보려면:" -ForegroundColor DarkGray
Write-Host "  Start-ScheduledTask -TaskName `"$taskName`"" -ForegroundColor DarkGray
