# 관리자 권한 PowerShell에서 한 번만 실행하세요.
# 1) 기존 "Albatross News Auto-Updater" 태스크: 10분 -> 1시간 간격으로 변경 + 활성화 (하루 종일 도는 전체 파이프라인 안전망)
# 2) 신규 "Albatross BoxScore Quick Update" 태스크 생성: 13:30~23:30 동안 10분마다 박스스코어만 가볍게 확인
#    (13:35 시작으로 5분 오프셋 — 기존 태스크가 매 정시(:00)에 도는 것과 겹치지 않게 해서 SQLite 동시 쓰기 충돌 가능성을 줄임)

$ErrorActionPreference = "Stop"

Write-Host "[1/2] 기존 전체 수집 태스크 간격을 1시간으로 변경하고 활성화합니다..." -ForegroundColor Cyan
schtasks /Change /TN "Albatross News Auto-Updater" /RI 60
Enable-ScheduledTask -TaskName "Albatross News Auto-Updater" | Out-Null
Write-Host "  -> 완료" -ForegroundColor Green

Write-Host "[2/2] 박스스코어 전용 시간대(13:30~23:30, 10분 간격, 13:35 시작) 태스크를 새로 만듭니다..." -ForegroundColor Cyan

$existingQuick = Get-ScheduledTask -TaskName "Albatross BoxScore Quick Update" -ErrorAction SilentlyContinue
if ($existingQuick) {
    Unregister-ScheduledTask -TaskName "Albatross BoxScore Quick Update" -Confirm:$false
    Write-Host "  -> 기존 동일 이름 태스크를 제거하고 새로 만듭니다." -ForegroundColor Yellow
}

$action = New-ScheduledTaskAction `
    -Execute "powershell.exe" `
    -Argument '-NoProfile -ExecutionPolicy Bypass -File "C:\works\Albatross\Run-Collector.ps1" -BoxScoreOnly' `
    -WorkingDirectory "C:\works\Albatross"

# New-ScheduledTaskTrigger -Daily는 -RepetitionInterval을 직접 지원하지 않아서,
# -Once 트리거로 반복 설정을 만든 뒤 그 Repetition만 Daily 트리거에 옮겨 붙인다.
$trigger = New-ScheduledTaskTrigger -Daily -At 1:35PM
$trigger.Repetition = (New-ScheduledTaskTrigger -Once -At 1:35PM `
    -RepetitionInterval (New-TimeSpan -Minutes 10) `
    -RepetitionDuration (New-TimeSpan -Hours 10)).Repetition

# 기존 전체 수집 태스크의 Principal/Settings(권한/배터리 정책 등)를 그대로 재사용해서 동일하게 맞춘다.
$fullPipelineTask = Get-ScheduledTask -TaskName "Albatross News Auto-Updater"

Register-ScheduledTask `
    -TaskName "Albatross BoxScore Quick Update" `
    -Action $action `
    -Trigger $trigger `
    -Principal $fullPipelineTask.Principal `
    -Settings $fullPipelineTask.Settings `
    -Description "13:30~23:30 동안 10분마다 방금 끝난 KBO 경기 박스스코어만 가볍게 확인/수집 (뉴스/KBO 전체 재수집/Gemma 하이라이트는 생략)" | Out-Null

Write-Host "  -> 완료" -ForegroundColor Green

Write-Host "`n=== 최종 상태 ===" -ForegroundColor Cyan
Get-ScheduledTask -TaskName "Albatross News Auto-Updater", "Albatross BoxScore Quick Update" |
    Select-Object TaskName, State
