# Albatross Keyword 주간 갱신
#
#   네이버 키워드 조사 → 화면 생성 → 커밋 → 푸시
#
# 월간 검색수는 네이버가 주는 월평균값이라 매일 돌려도 값이 같다.
# 반면 블로그 경쟁(제목 일치·최신 글)은 새 글이 올라오며 계속 바뀌므로
# 주 1회가 적당하다. 측정값은 KeywordMetrics에 쌓여 추이가 된다.
#
# 수동 실행:  powershell -ExecutionPolicy Bypass -File .\Run-KeywordUpdate.ps1
# 확인만:     ... -File .\Run-KeywordUpdate.ps1 -NoPush
# 시드 교체:  ... -File .\Run-KeywordUpdate.ps1 -Seeds "주식","금리"

param(
    [string[]]$Seeds = @("주식","금리","부동산","재테크","창업","세금","연금","대출","환율","보험"),
    [int]$PerTier = 25,
    [switch]$NoPush
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$siteRoot = Join-Path (Split-Path -Parent $repoRoot) "AlbatrossKeyword"
$logDir   = Join-Path $repoRoot "logs"
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }
$logFile  = Join-Path $logDir ("keyword-update-{0}.log" -f (Get-Date -Format "yyyyMMdd"))

function Write-Log {
    param([string]$Message, [string]$Color = "Gray")
    $line = "[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $Message
    Write-Host $line -ForegroundColor $Color
    Add-Content -Path $logFile -Value $line -Encoding utf8
}

Write-Log "=== Albatross Keyword 갱신 시작 ===" "Cyan"
Write-Log ("시드 {0}개: {1}" -f $Seeds.Count, ($Seeds -join ", "))

# 환경변수를 User 범위에서 읽어 현재 프로세스에 넣는다.
# 이렇게 하지 않으면 이 스크립트를 띄운 셸이 변수 등록 전에 열렸을 때
# 자식 dotnet 프로세스가 키를 못 받아 검색량이 통째로 빠진다(실제로 그렇게 실패했다).
foreach ($name in @("NAVER_AD_API_KEY","NAVER_AD_SECRET_KEY","NAVER_AD_CUSTOMER_ID","NAVER_CLIENT_ID","NAVER_CLIENT_SECRET")) {
    $v = [Environment]::GetEnvironmentVariable($name,"User")
    if ($v) { Set-Item -Path ("Env:" + $name) -Value $v }
}
if (-not $env:NAVER_AD_API_KEY) {
    Write-Log "NAVER_AD_API_KEY 가 없습니다. 월간 검색수 없이 진행됩니다." "Yellow"
}

# 1. 조사 — 자동완성 확장 + 검색광고 연관어 + 블로그 경쟁 확인
Write-Log "[1/3] 키워드 조사..." "Cyan"
Push-Location $repoRoot
try {
    $dotnetArgs = @("run","--project","Albatross.Collector","--configuration","Release","--","--keyword") `
          + $Seeds + @("--depth","1","--max","10","--per-tier","$PerTier")
    & dotnet @dotnetArgs 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Log "수집기 실행 실패 (exit $LASTEXITCODE)" "Red"
        exit 1
    }

    # 2. 화면 생성
    Write-Log "[2/3] 화면 생성..." "Cyan"
    & dotnet run --project Albatross.Collector --configuration Release -- --export-keywords 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Log "화면 생성 실패" "Red"; exit 1 }
} finally { Pop-Location }

# 3. 변경분이 있을 때만 커밋 — 매주 의미 없는 커밋이 쌓이지 않게
if (-not (Test-Path $siteRoot)) { Write-Log "사이트 폴더 없음: $siteRoot" "Red"; exit 1 }
Push-Location $siteRoot
try {
    $changed = & git status --porcelain
    if (-not $changed) {
        Write-Log "[3/3] 변경된 내용이 없습니다. 배포를 건너뜁니다." "Yellow"
        Write-Log "=== 완료 ===" "Cyan"
        exit 0
    }
    Write-Log ("[3/3] 변경 파일 {0}개" -f ($changed | Measure-Object).Count) "Cyan"

    if ($NoPush) { Write-Log "-NoPush 지정 — 커밋하지 않고 종료합니다." "Yellow"; exit 0 }

    & git add -A
    & git commit -m ("키워드 갱신 ({0})" -f (Get-Date -Format "yyyy-MM-dd")) | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Log "커밋할 내용이 없습니다." "Yellow"; exit 0 }

    & git push origin main
    if ($LASTEXITCODE -ne 0) { Write-Log "푸시 실패 — 네트워크나 인증을 확인하세요." "Red"; exit 1 }
    Write-Log "푸시 완료. GitHub Actions가 배포를 진행합니다." "Green"
} finally { Pop-Location }

Write-Log "=== 완료 ===" "Cyan"
