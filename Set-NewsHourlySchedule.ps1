# 관리자 권한 PowerShell에서 한 번만 실행하세요. (작업 스케줄러 수정은 관리자 권한 필요)
# "Albatross News Auto-Updater" 태스크(Run-Collector.ps1 전체 파이프라인)를
#   - 반복 간격 10분 -> 1시간(PT1H)으로 변경
#   - 비활성 상태이면 활성화
#
# 이 태스크는 "로그온한 동안만 실행(Interactive)" 설정이라 비밀번호가 필요 없습니다.
# (schtasks 명령은 습관적으로 비밀번호를 물어보므로, 여기서는 순수 PowerShell 방식만 사용해 비밀번호 프롬프트를 피합니다.)

$ErrorActionPreference = "Stop"
$taskName = "Albatross News Auto-Updater"

Write-Host "[1/2] '$taskName' 반복 간격을 1시간으로 변경합니다..." -ForegroundColor Cyan
$task = Get-ScheduledTask -TaskName $taskName

# 기존 트리거의 반복 간격만 1시간으로 수정 (StartBoundary 등 나머지는 그대로 유지)
$task.Triggers[0].Repetition.Interval = "PT1H"

# 계정을 그대로(로그온 시에만 실행 + 최고 권한) 다시 지정 -> 비밀번호 저장이 필요 없어 프롬프트가 뜨지 않음
$principal = New-ScheduledTaskPrincipal -UserId $task.Principal.UserId -LogonType Interactive -RunLevel Highest

Set-ScheduledTask -TaskName $taskName -Trigger $task.Triggers[0] -Principal $principal | Out-Null

Write-Host "[2/2] 태스크를 활성화합니다..." -ForegroundColor Cyan
Enable-ScheduledTask -TaskName $taskName | Out-Null

Write-Host "`n=== 최종 상태 (State=Ready, Interval=PT1H 이면 성공) ===" -ForegroundColor Cyan
$t = Get-ScheduledTask -TaskName $taskName
[PSCustomObject]@{
    TaskName = $t.TaskName
    State    = $t.State
    Interval = $t.Triggers[0].Repetition.Interval
    Args     = $t.Actions[0].Arguments
} | Format-List
