# 뉴스 키워드 추출 실행 스크립트 (작업 스케줄러가 하루 1~2회 호출)
# RawNews의 오늘 뉴스에서 급상승 키워드를 통계+로컬 Gemma로 뽑아 NewsKeywords 테이블에 "갱신"한다.
# (뉴스 수집과 달리 git 커밋/푸시는 하지 않음 — DB에만 저장)

$ErrorActionPreference = "Stop"
$LogFile = Join-Path $PSScriptRoot "keyword_log.txt"

function Write-Log {
    param([string]$Message)
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    "[$ts] $Message" | Out-File -FilePath $LogFile -Append -Encoding utf8
    Write-Host $Message
}

Write-Log "키워드 추출 시작..."
try {
    dotnet run --project (Join-Path $PSScriptRoot "Albatross.Collector\Albatross.Collector.csproj") --configuration Release -- --extract-keywords
    Write-Log "키워드 추출 완료."
} catch {
    Write-Log "[오류] $($_.Exception.Message)"
    exit 1
}
