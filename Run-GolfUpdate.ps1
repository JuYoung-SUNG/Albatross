# Albatross Golf 주간 갱신
#
#   KLPGA 대회 일정·우승자 수집 → 부문별 랭킹 수집 → 정적 페이지 생성 → 커밋 → 푸시
#
# 대회는 대부분 일요일에 끝나므로 월요일 아침에 한 번 돌리면
# 지난주 우승자와 갱신된 상금랭킹이 사이트에 반영된다.
#
# 수동 실행:  powershell -ExecutionPolicy Bypass -File .\Run-GolfUpdate.ps1
# 밀어넣기 없이 확인만:  ... -File .\Run-GolfUpdate.ps1 -NoPush

param(
    [int]$Season = 0,      # 0이면 올해
    [switch]$NoPush        # 커밋·푸시를 건너뛰고 생성까지만
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$siteRoot = Join-Path (Split-Path -Parent $repoRoot) "AlbatrossGolf"
$logDir   = Join-Path $repoRoot "logs"
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }
$logFile  = Join-Path $logDir ("golf-update-{0}.log" -f (Get-Date -Format "yyyyMMdd"))

function Write-Log {
    param([string]$Message, [string]$Color = "Gray")
    $line = "[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $Message
    Write-Host $line -ForegroundColor $Color
    Add-Content -Path $logFile -Value $line -Encoding utf8
}

if ($Season -eq 0) { $Season = (Get-Date).Year }

Write-Log "=== Albatross Golf 갱신 시작 (시즌 $Season) ===" "Cyan"

# 1. 수집 + 페이지 생성 (대회·랭킹·사이트가 한 번에 처리된다)
Write-Log "[1/3] KLPGA 대회·기록 수집 및 페이지 생성..." "Cyan"
Push-Location $repoRoot
try {
    $output = & dotnet run --project Albatross.Collector --configuration Release -- --collect-tournaments $Season 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Log "수집기 실행 실패 (exit $LASTEXITCODE)" "Red"
        $output | Select-Object -Last 15 | ForEach-Object { Write-Log "  $_" "DarkGray" }
        exit 1
    }
} finally { Pop-Location }
Write-Log "수집 완료" "Green"

# 2. 변경분 확인 — 바뀐 게 없으면 굳이 커밋하지 않는다
if (-not (Test-Path $siteRoot)) {
    Write-Log "사이트 폴더를 찾을 수 없습니다: $siteRoot" "Red"
    exit 1
}
Push-Location $siteRoot
try {
    $changed = & git status --porcelain
    if (-not $changed) {
        Write-Log "[2/3] 변경된 내용이 없습니다. 배포를 건너뜁니다." "Yellow"
        Write-Log "=== 완료 ===" "Cyan"
        exit 0
    }
    $fileCount = ($changed | Measure-Object).Count
    Write-Log "[2/3] 변경 파일 $fileCount 개" "Cyan"

    if ($NoPush) {
        Write-Log "-NoPush 지정 — 커밋하지 않고 종료합니다." "Yellow"
        exit 0
    }

    # 3. 커밋 + 푸시 → GitHub Actions가 Cloudflare Pages로 배포
    Write-Log "[3/3] 커밋 및 푸시..." "Cyan"
    & git add -A
    $msg = "대회·기록 갱신 ({0} 시즌, {1})" -f $Season, (Get-Date -Format "yyyy-MM-dd")
    & git commit -m $msg | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Log "커밋할 내용이 없습니다." "Yellow"
        exit 0
    }
    & git push origin main
    if ($LASTEXITCODE -ne 0) {
        Write-Log "푸시 실패 — 네트워크나 인증을 확인하세요." "Red"
        exit 1
    }
    Write-Log "푸시 완료. GitHub Actions가 배포를 진행합니다." "Green"
} finally { Pop-Location }

Write-Log "=== 완료 ===" "Cyan"
