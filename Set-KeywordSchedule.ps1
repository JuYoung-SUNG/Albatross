# 관리자 권한 PowerShell에서 한 번만 실행하세요. (작업 스케줄러 등록은 관리자 권한 필요)
# "Albatross Keyword Extract" 태스크를 만들어 매일 08:00, 20:00에 키워드 추출을 자동 실행합니다.
# (로그온한 동안 실행 = 비밀번호 불필요. 뉴스 수집 태스크와 시간대를 겹치지 않게 배치)

$ErrorActionPreference = "Stop"
$taskName = "Albatross Keyword Extract"

Write-Host "[1/2] 기존 동일 태스크가 있으면 제거..." -ForegroundColor Cyan
if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}

Write-Host "[2/2] 새 태스크 등록 (매일 08:00, 20:00)..." -ForegroundColor Cyan

$action = New-ScheduledTaskAction `
    -Execute "powershell.exe" `
    -Argument '-NoProfile -ExecutionPolicy Bypass -File "C:\works\Albatross\Run-KeywordExtract.ps1"' `
    -WorkingDirectory "C:\works\Albatross"

# 하루 2회 트리거 (원하면 시간 조정: 아래 -At 값만 바꾸면 됨)
$t1 = New-ScheduledTaskTrigger -Daily -At 8:00AM
$t2 = New-ScheduledTaskTrigger -Daily -At 8:00PM

# 로그온 시에만 실행(Interactive) + 현재 사용자 → 비밀번호 저장 불필요
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest

Register-ScheduledTask `
    -TaskName $taskName `
    -Action $action `
    -Trigger @($t1, $t2) `
    -Principal $principal `
    -Description "RawNews의 오늘 뉴스에서 급상승 키워드를 통계+로컬 Gemma로 추출해 NewsKeywords에 갱신 (하루 2회)" | Out-Null

Write-Host "`n=== 등록 완료 ===" -ForegroundColor Green
$t = Get-ScheduledTask -TaskName $taskName
[PSCustomObject]@{
    TaskName = $t.TaskName
    State    = $t.State
    실행시각 = ($t.Triggers | ForEach-Object { $_.StartBoundary.Substring(11,5) }) -join ", "
} | Format-List
