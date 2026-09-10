# "Albatross Keyword Weekly" 작업을 등록한다.
#
# 월요일 08:30. 골프 갱신(08:00) 뒤에 돌도록 30분 띄웠다.
#
# 왜 주 1회인가 —
#   월간 검색수는 네이버가 주는 월평균값이라 매일 재도 같은 값이다.
#   반면 블로그 경쟁(제목 일치·최신 글)은 새 글이 올라오며 계속 바뀐다.
#   주 1회면 추이를 보기에 충분하고 API도 하루 한도의 0.3%만 쓴다.
#
# 해제:  Unregister-ScheduledTask -TaskName "Albatross Keyword Weekly" -Confirm:$false

$ErrorActionPreference = "Stop"

$taskName = "Albatross Keyword Weekly"
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$script   = Join-Path $repoRoot "Run-KeywordUpdate.ps1"

if (-not (Test-Path $script)) {
    Write-Host "Run-KeywordUpdate.ps1 을 찾을 수 없습니다: $script" -ForegroundColor Red
    exit 1
}

try {
    Get-ScheduledTask -TaskName $taskName -ErrorAction Stop | Out-Null
    Write-Host "기존 작업을 제거하고 다시 등록합니다..." -ForegroundColor Yellow
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
} catch { }

$action = New-ScheduledTaskAction `
    -Execute "powershell.exe" `
    -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$script`"" `
    -WorkingDirectory $repoRoot

$trigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek Monday -At 8:30AM

# 로그온한 동안만 실행 -> 비밀번호 저장이 필요 없어 프롬프트가 뜨지 않는다
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive

$settings = New-ScheduledTaskSettingsSet `
    -StartWhenAvailable `
    -DontStopIfGoingOnBatteries `
    -AllowStartIfOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Hours 1)

Register-ScheduledTask -TaskName $taskName `
    -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
    -Description "네이버 키워드를 조사해 Albatross Keyword 화면을 갱신하고 배포합니다." | Out-Null

Write-Host "`n=== 등록 완료 ===" -ForegroundColor Cyan
$t = Get-ScheduledTask -TaskName $taskName
$i = Get-ScheduledTaskInfo -TaskName $taskName
[PSCustomObject]@{ TaskName = $t.TaskName; State = $t.State; NextRun = $i.NextRunTime } | Format-List

Write-Host "지금 바로 돌려보려면:" -ForegroundColor DarkGray
Write-Host "  Start-ScheduledTask -TaskName `"$taskName`"" -ForegroundColor DarkGray
