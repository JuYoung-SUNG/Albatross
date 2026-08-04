# Albatross 뉴스 수집 및 자동 GitHub 푸시 스크립트
# 이 스크립트는 로컬에서 실행되어 뉴스를 수집하고 결과를 GitHub에 업데이트합니다.
# -BoxScoreOnly: 저녁 시간대에 짧은 주기로 돌리기 위한 가벼운 모드 — 뉴스/KBO 전체 재수집/Gemma 하이라이트는
#                건너뛰고 방금 끝난 경기의 박스스코어만 확인해서 kbo-games.json만 갱신한다.
param(
    [switch]$BoxScoreOnly
)

$ErrorActionPreference = "Stop"
$LogFile = Join-Path $PSScriptRoot "update_log.txt"

function Write-Log {
    param([string]$Message, [string]$Color = "White")
    $Timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $FullMessage = "[$Timestamp] $Message"
    Write-Host $Message -ForegroundColor $Color
    # 한글 깨짐 방지를 위해 인코딩을 utf8로 명시 (Windows PowerShell의 경우 기본값이 다를 수 있음)
    $FullMessage | Out-File -FilePath $LogFile -Append -Encoding utf8
}

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  Albatross News Collector Auto-Updater   " -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

try {
    if ($BoxScoreOnly) {
        # 1. 박스스코어 전용 가벼운 수집 (뉴스/KBO 전체 재수집/Gemma 하이라이트 생략)
        Write-Log "[1/3] 박스스코어 전용 수집 시작..."
        dotnet run --project Albatross.Collector/Albatross.Collector.csproj --configuration Release -- --once --boxscore-only

        # 2. Git 변경 사항 확인 및 커밋 (kbo-games.json만 대상)
        Write-Log "[2/3] 변경 사항 확인 및 Git 커밋 중..."
        git add "Albatross.Web/wwwroot/data/kbo-games.json"

        $status = git status --porcelain "Albatross.Web/wwwroot/data/kbo-games.json"
        if ($status) {
            $date = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
            git commit -m "Auto-update box scores (Local: $date)"

            # 3. GitHub로 푸시
            Write-Log "[3/3] GitHub로 푸시 중..."
            git push
            Write-Log "[성공] 박스스코어 데이터가 성공적으로 업데이트되었습니다." "Green"
        } else {
            Write-Log "[정보] 업데이트할 새로운 박스스코어가 없습니다." "Yellow"
        }
    } else {
        # 1. 뉴스/KBO 수집기 실행 (1회 실행 모드 — 뉴스, KBO 팀순위/선수기록/박스스코어/하이라이트 전부 포함)
        Write-Log "[1/3] 뉴스 및 KBO 데이터 수집 시작..."
        dotnet run --project Albatross.Collector/Albatross.Collector.csproj --configuration Release -- --once

        # 2. Git 변경 사항 확인 및 커밋
        Write-Log "[2/3] 변경 사항 확인 및 Git 커밋 중..."

        # news.json + kbo-*.json (대시보드/날짜별경기/선수기록/팀순위 정적 데이터) 추가
        git add Albatross.Web/wwwroot/data/news.json
        git add "Albatross.Web/wwwroot/data/kbo-*.json"

        # 변경 사항이 있는지 확인
        $status = git status --porcelain Albatross.Web/wwwroot/data/news.json "Albatross.Web/wwwroot/data/kbo-*.json"
        if ($status) {
            $date = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
            git commit -m "Auto-update news & KBO data (Local: $date)"

            # 3. GitHub로 푸시
            Write-Log "[3/3] GitHub로 푸시 중..."
            git push
            Write-Log "[성공] 뉴스/KBO 데이터가 성공적으로 업데이트되었습니다." "Green"
        } else {
            Write-Log "[정보] 업데이트할 새로운 데이터가 없습니다." "Yellow"
        }
    }
} catch {
    Write-Log "[오류] 작업 중 에러 발생: $($_.Exception.Message)" "Red"
    exit 1
}

Write-Host "`n==========================================" -ForegroundColor Cyan
Write-Host "작업이 완료되었습니다." -ForegroundColor Cyan
