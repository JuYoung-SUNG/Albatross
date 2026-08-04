using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Albatross.Collector.News.Models;
using Albatross.Collector.News.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Albatross.Collector
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IEnumerable<INewsService> _newsServices;
        private readonly KboOfficialSiteService _kboOfficialSite;
        private readonly NaverNewsService _naverNews;
        private readonly GemmaClassificationService _classifier;
        private readonly KeywordExtractionService _keywordExtractor;
        private readonly IConfiguration _config;
        private readonly HttpClient _httpClient;
        private readonly IHostApplicationLifetime _appLifetime;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        public Worker(
            ILogger<Worker> logger,
            IEnumerable<INewsService> newsServices,
            KboOfficialSiteService kboOfficialSite,
            NaverNewsService naverNews,
            GemmaClassificationService classifier,
            KeywordExtractionService keywordExtractor,
            IConfiguration config,
            IHttpClientFactory httpClientFactory,
            IHostApplicationLifetime appLifetime)
        {
            _logger = logger;
            _newsServices = newsServices;
            _kboOfficialSite = kboOfficialSite;
            _naverNews = naverNews;
            _keywordExtractor = keywordExtractor;
            _classifier = classifier;
            _config = config;
            _httpClient = httpClientFactory.CreateClient();
            _httpClient.Timeout = TimeSpan.FromMinutes(5);
            _appLifetime = appLifetime;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Collector worker starting");

            var singleRun = Environment.GetCommandLineArgs().Contains("--once");
            // 저녁 시간대에 짧은 주기로 돌리기 위한 가벼운 모드 — 뉴스 수집/KBO 전체 재수집/Gemma 하이라이트는 건너뛰고
            // 방금 끝난 경기의 박스스코어만 확인해서 가져온 뒤 kbo-games.json만 다시 내보낸다.
            var boxScoreOnly = Environment.GetCommandLineArgs().Contains("--boxscore-only");
            // 시즌 시작 달부터 경기결과+박스스코어를 소급 수집하는 1회성 모드 (팀순위/선수기록은 누적 스냅샷 페이지라 소급 불가)
            var backfillSeason = Environment.GetCommandLineArgs().Contains("--backfill-season");
            // 팀 순위(승률/게임차) 날짜별 이력을 소급 수집하는 1회성 모드 — TeamRankDaily 페이지만 날짜 조회가 가능해서
            // 팀타율/출루율/방어율/피안타율은 대상에서 제외됨(그 값들은 NULL로 남고, 앞으로 계속 실시간으로만 쌓임)
            var backfillStandings = Environment.GetCommandLineArgs().Contains("--backfill-standings");
            // 팀타율/출루율/방어율/피안타율 + 선수(타자/투수) 홈런 등 개인 기록을 소급 채우는 1회성 모드 —
            // KBO 사이트가 아니라 이미 수집된 박스스코어(--backfill-season 결과물)를 날짜순으로 누적 집계해서 재구성
            var backfillExtraStats = Environment.GetCommandLineArgs().Contains("--backfill-extra-stats");
            // 네이버 스포츠 KBO 뉴스 날짜별 소급 수집: --backfill-news [시작일 yyyy-MM-dd] [종료일 yyyy-MM-dd]
            // 날짜 인자를 안 주면 어제 하루만 수집한다.
            var backfillNews = Environment.GetCommandLineArgs().Contains("--backfill-news");
            // RawNews에서 본문이 비어 있는 네이버 기사들의 원문을 크롤링해 Content를 채우는 1회성 모드 (재실행 시 이어서 진행)
            var fillNewsContent = Environment.GetCommandLineArgs().Contains("--fill-news-content");
            // RawNews에서 급상승 키워드를 통계+Gemma로 뽑아 NewsKeywords에 저장하는 1회성 모드 (블로그 소재용)
            var extractKeywords = Environment.GetCommandLineArgs().Contains("--extract-keywords");

            if (backfillSeason)
            {
                await RunSeasonBackfillAsync(stoppingToken);
                _appLifetime.StopApplication();
                return;
            }

            if (backfillStandings)
            {
                await RunStandingsBackfillAsync(stoppingToken);
                _appLifetime.StopApplication();
                return;
            }

            if (backfillExtraStats)
            {
                await RunExtraStatsBackfillAsync(stoppingToken);
                _appLifetime.StopApplication();
                return;
            }

            if (backfillNews)
            {
                await RunNewsBackfillAsync(stoppingToken);
                _appLifetime.StopApplication();
                return;
            }

            if (fillNewsContent)
            {
                var databasePathForFill = ResolveDatabasePath();
                await InitializeDatabaseAsync(databasePathForFill, stoppingToken);
                await _naverNews.FillMissingContentAsync(databasePathForFill, stoppingToken);
                _appLifetime.StopApplication();
                return;
            }

            if (extractKeywords)
            {
                await RunKeywordExtractionAsync(stoppingToken);
                _appLifetime.StopApplication();
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("Collector tick at: {time}", DateTimeOffset.Now);

                    var databasePath = ResolveDatabasePath();

                    await InitializeDatabaseAsync(databasePath, stoppingToken);

                    // 1. 날짜 및 차수 계산
                    var nowKst = GetKoreaNow();
                    var dateStr = nowKst.ToString("yyyyMMdd");
                    var tick = await GetNextTickAsync(databasePath, dateStr, stoppingToken);
                    var tickStr = tick.ToString("D3");

                    if (!boxScoreOnly)
                    {
                        var fetchedItems = new List<NewsItem>();
                        foreach (var newsService in _newsServices)
                        {
                            try
                            {
                                var items = (await newsService.GetLatestAsync(stoppingToken)).ToList();
                                fetchedItems.AddRange(items);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to fetch from news source {source}", newsService.GetType().Name);
                            }
                        }
                        _logger.LogInformation("Fetched {count} news items", fetchedItems.Count);

                        // 소스별 마지막 수집 항목 로그 (실제 데이터 형태를 확인할 수 있도록 전문 출력)
                        foreach (var group in fetchedItems.GroupBy(i => i.Source))
                        {
                            var last = group.OrderByDescending(i => i.PublishedAt).First();
                            _logger.LogInformation(
                                "[{source}] 마지막 뉴스 전문\n" +
                                "  제목: {title}\n" +
                                "  발행: {publishedAt:yyyy-MM-dd HH:mm:ss}\n" +
                                "  URL: {url}\n" +
                                "  카테고리: {category} / 국가: {country}\n" +
                                "  이미지: {imageUrl}\n" +
                                "  요약: {summary}\n" +
                                "  본문: {content}",
                                group.Key, last.Title, last.PublishedAt, last.Url,
                                last.Category, last.Country,
                                last.ImageUrl ?? "(없음)",
                                last.Summary,
                                last.Content ?? "(없음)");
                        }

                        _logger.LogInformation("Starting SQLite save for raw news rows. Count: {count}. Date: {date}, Tick: {tick}", fetchedItems.Count, dateStr, tickStr);
                        var insertedOrUpdated = await SaveRawNewsAsync(databasePath, fetchedItems, dateStr, tickStr, stoppingToken);
                        _logger.LogInformation("Completed SQLite save for raw news rows. Saved rows: {count}", insertedOrUpdated);

                        // [임시 비활성화] 현재는 "뉴스 크롤링 + RawNews 저장"만 수행한다.
                        // KBO 팀순위/선수기록/경기결과 수집은 아래처럼 중단 (필요 시 주석 해제하면 원복).
                        // await _kboOfficialSite.CollectAndSaveAsync(databasePath, stoppingToken);
                    }
                    else
                    {
                        // 가벼운 모드: 팀순위/선수기록은 건너뛰되, 경기 스코어(끝났는지 여부)만큼은 갱신해야
                        // 아래 CollectAndSaveBoxScoresAsync가 "방금 끝난 경기"를 인식할 수 있다.
                        await _kboOfficialSite.CollectAndSaveGameResultsAsync(databasePath, stoppingToken);
                    }

                    // 종료된 경기의 개인 박스스코어 수집 — [임시] 뉴스 전용 모드에선 KBO 수집을 전부 멈추므로,
                    // 박스스코어 전용 가벼운 모드(--boxscore-only)에서만 실행한다.
                    if (boxScoreOnly)
                    {
                        await _kboOfficialSite.CollectAndSaveBoxScoresAsync(databasePath, stoppingToken);

                        // 가벼운 모드: 박스스코어 변경으로 값이 바뀔 수 있는 kbo-games.json만 다시 내보낸다
                        await ExportGamesOnlyAsync(databasePath, stoppingToken);
                    }
                    // [임시 비활성화] 뉴스만 수집/저장하도록, 일반 모드의 KBO 하이라이트 생성과 KBO JSON 내보내기를 중단.
                    // (뉴스 크롤링 결과는 위 SaveRawNewsAsync에서 이미 RawNews에 저장됨. 필요 시 아래 주석 해제로 원복)
                    // else
                    // {
                    //     await RunKboDateHighlightAsync(databasePath, stoppingToken);   // KBO 하이라이트(Gemma) 생성
                    //     await ExportKboDataToJsonAsync(databasePath, stoppingToken);    // KBO 데이터 JSON 내보내기
                    // }

                    // --- 카테고리 분류 로직 (당분간 비활성화 - 수집 기반부터 다지는 단계) ---
                    // await RunCategoryClassificationAsync(databasePath, stoppingToken);
                    // ----------------------------

                    // [Phase 3] 동일 카테고리 집중 분석 및 요약 (당분간 비활성화)
                    // var rawItemsForSummary = await LoadRawNewsForSummaryAsync(databasePath, stoppingToken);
                    // _logger.LogInformation("Loaded {count} new news items for Phase 3 summary analysis", rawItemsForSummary.Count);
                    //
                    // if (rawItemsForSummary.Count > 0)
                    // {
                    //     var summarizedNews = (await AnalyzeNewsWithAI(databasePath, rawItemsForSummary, stoppingToken)).ToList();
                    //     _logger.LogInformation("Gemini Phase 3 returned {count} summarized news rows", summarizedNews.Count);
                    //
                    //     if (summarizedNews.Count > 0)
                    //     {
                    //         await SavePhase3SummariesAsync(databasePath, summarizedNews, stoppingToken);
                    //     }
                    // }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during collection");
                }

                if (singleRun)
                {
                    _logger.LogInformation("Single-run mode, exiting");
                    _logger.LogInformation(" === NEWS SCHEDULE FINISH ==== ");
                    _appLifetime.StopApplication();
                    break;
                }

                var intervalMinutes = _config.GetValue<int>("Collector:IntervalMinutes", 10);
                var delayTime = TimeSpan.FromMinutes(intervalMinutes);

                await Task.Delay(delayTime, stoppingToken);
            }

            _logger.LogInformation("Collector worker stopping");
        }

        private async Task<string?> GetNewsIdByUrlAsync(string databasePath, string url, CancellationToken ct)
        {
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(ct);

            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Id FROM RawNews WHERE Url = $url;";
            cmd.Parameters.AddWithValue("$url", url);
            
            return await cmd.ExecuteScalarAsync(ct) as string;
        }

        private static string ResolveDataDirectory()
        {
            var configuredDataDir =
                Environment.GetEnvironmentVariable("COLLECTOR_DATA_DIR")
                ?? Environment.GetEnvironmentVariable("Collector__DataDirectory");

            if (!string.IsNullOrWhiteSpace(configuredDataDir))
            {
                return Path.GetFullPath(configuredDataDir);
            }

            var baseDir = AppContext.BaseDirectory;

            if (baseDir.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                var solutionRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
                return Path.Combine(solutionRoot, "Albatross.Web", "wwwroot", "data");
            }

            return Path.Combine(baseDir, "data");
        }

        private string ResolveDatabasePath()
        {
            var dataDir = ResolveDataDirectory();
            Directory.CreateDirectory(dataDir);

            var databasePath = _config["Collector:DatabasePath"];
            if (string.IsNullOrWhiteSpace(databasePath))
            {
                databasePath = Path.Combine(dataDir, "albatross-news.db");
            }
            else
            {
                databasePath = Path.GetFullPath(databasePath);
            }

            var databaseDirectory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrWhiteSpace(databaseDirectory))
            {
                Directory.CreateDirectory(databaseDirectory);
            }

            return databasePath;
        }

        /// <summary>
        /// --backfill-season 1회성 모드 — 시즌 시작 달(3월)부터 이번 달까지 경기결과를 소급 수집하고,
        /// 그 경기들의 박스스코어까지 이어서 수집한다 (경기 수에 따라 시간이 오래 걸릴 수 있음).
        /// </summary>
        private async Task RunSeasonBackfillAsync(CancellationToken ct)
        {
            var databasePath = ResolveDatabasePath();
            await InitializeDatabaseAsync(databasePath, ct);

            var nowKst = GetKoreaNow();
            const int seasonStartMonth = 3;

            _logger.LogInformation("[시즌 소급] {year}년 {startMonth}월부터 {endMonth}월까지 경기결과 수집 시작...", nowKst.Year, seasonStartMonth, nowKst.Month);
            await _kboOfficialSite.BackfillSeasonGamesAsync(databasePath, nowKst.Year, seasonStartMonth, ct);

            _logger.LogInformation("[시즌 소급] 박스스코어 수집 시작 (경기 수가 많으면 오래 걸릴 수 있습니다)...");
            await _kboOfficialSite.CollectAndSaveBoxScoresAsync(databasePath, ct);

            await ExportGamesOnlyAsync(databasePath, ct);
            _logger.LogInformation("[시즌 소급] 완료");
        }

        /// <summary>
        /// --backfill-standings 1회성 모드 — 시즌 시작일(3월 28일)부터 어제까지 팀 순위(승률/게임차) 이력을
        /// 날짜별로 소급 수집한다. 오늘 날짜는 실시간 틱이 이미 완전한 값(팀타율 등 포함)으로 채우므로 제외한다.
        /// </summary>
        private async Task RunStandingsBackfillAsync(CancellationToken ct)
        {
            var databasePath = ResolveDatabasePath();
            await InitializeDatabaseAsync(databasePath, ct);

            var nowKst = GetKoreaNow();
            var seasonStart = new DateOnly(nowKst.Year, 3, 28);
            var lastDate = DateOnly.FromDateTime(nowKst).AddDays(-1);

            if (lastDate < seasonStart)
            {
                _logger.LogInformation("[팀순위 소급] 소급할 날짜 범위가 없습니다 (시즌 시작 전).");
                return;
            }

            _logger.LogInformation("[팀순위 소급] {start}부터 {end}까지 팀 순위 이력 수집 시작 (팀타율/출루율/방어율/피안타율은 이 방식으로는 소급 불가하여 NULL로 저장됩니다)...", seasonStart, lastDate);
            await _kboOfficialSite.BackfillTeamStandingsAsync(databasePath, seasonStart, lastDate, ct);

            // 주의: 여기서 export를 다시 돌리면 안 된다 — 소급 데이터의 CollectedAt(어제 05:00:00)이
            // 아직 오늘자 실시간 틱보다 최신일 수 있어서, ExportStandingsAsync의 MAX(CollectedAt)이
            // 팀타율 등이 NULL인 이 소급 행을 "최신"으로 잘못 골라 kbo-standings.json을 훼손한다.
            // 현재 스냅샷 재계산은 다음 실시간 틱에 맡긴다.
            _logger.LogInformation("[팀순위 소급] 완료");
        }

        /// <summary>
        /// --backfill-extra-stats 1회성 모드 — 팀타율/출루율/방어율/피안타율과 선수(타자/투수) 홈런 등을
        /// 이미 수집된 박스스코어에서 날짜별로 재구성한다. KBO 사이트에 다시 접속하지 않는 순수 로컬 집계라
        /// --backfill-season으로 박스스코어가 먼저 채워져 있어야 하고, --backfill-standings로 KboTeamStandings에
        /// 날짜별 행이 먼저 있어야 팀 스탯 UPDATE 대상을 찾을 수 있다(선행 조건).
        /// </summary>
        private async Task RunExtraStatsBackfillAsync(CancellationToken ct)
        {
            var databasePath = ResolveDatabasePath();
            await InitializeDatabaseAsync(databasePath, ct);

            var nowKst = GetKoreaNow();
            var seasonStart = new DateOnly(nowKst.Year, 3, 28);
            var lastDate = DateOnly.FromDateTime(nowKst).AddDays(-1);

            if (lastDate < seasonStart)
            {
                _logger.LogInformation("[팀/선수 스탯 소급] 소급할 날짜 범위가 없습니다 (시즌 시작 전).");
                return;
            }

            _logger.LogInformation("[팀/선수 스탯 소급] {start}부터 {end}까지 박스스코어 기반 집계 시작...", seasonStart, lastDate);
            await _kboOfficialSite.BackfillTeamExtraStatsAsync(databasePath, seasonStart, lastDate, ct);
            await _kboOfficialSite.BackfillPlayerHomeRunHistoryAsync(databasePath, seasonStart, lastDate, ct);

            // 팀순위 소급 때와 동일한 이유로 export는 여기서 호출하지 않는다 — 다음 실시간 틱에 맡긴다.
            _logger.LogInformation("[팀/선수 스탯 소급] 완료");
        }

        /// <summary>
        /// --backfill-news 1회성 모드 — 네이버 스포츠 KBO리그 뉴스의 날짜별 목록 API에서 지정한 기간의
        /// 뉴스를 발행일 기준으로 소급 수집해 RawNews에 저장한다. 이미 저장된 URL은 자동으로 건너뛴다.
        /// 사용법: --backfill-news [시작일 yyyy-MM-dd] [종료일 yyyy-MM-dd] (인자 없으면 어제 하루)
        /// </summary>
        private async Task RunNewsBackfillAsync(CancellationToken ct)
        {
            var databasePath = ResolveDatabasePath();
            await InitializeDatabaseAsync(databasePath, ct);

            var parsedDates = Environment.GetCommandLineArgs()
                .Select(a => DateOnly.TryParseExact(a, "yyyy-MM-dd", out var d) ? d : (DateOnly?)null)
                .Where(d => d is not null)
                .Select(d => d!.Value)
                .OrderBy(d => d)
                .ToList();

            var yesterday = DateOnly.FromDateTime(GetKoreaNow()).AddDays(-1);
            var startDate = parsedDates.Count >= 1 ? parsedDates[0] : yesterday;
            var endDate = parsedDates.Count >= 2 ? parsedDates[^1] : startDate;

            _logger.LogInformation("[뉴스 소급] 네이버 스포츠 KBO 뉴스 — {start}부터 {end}까지 발행일 기준 수집 시작...", startDate, endDate);

            var totalSaved = 0;
            for (var date = startDate; date <= endDate; date = date.AddDays(1))
            {
                ct.ThrowIfCancellationRequested();

                var items = await _naverNews.GetKboNewsByDateAsync(date, ct);
                if (items.Count == 0) continue;

                var dateStr = date.ToString("yyyyMMdd");
                var tick = await GetNextTickAsync(databasePath, dateStr, ct);
                var saved = await SaveRawNewsAsync(databasePath, items, dateStr, tick.ToString("D3"), ct);
                totalSaved += saved;
                _logger.LogInformation("[뉴스 소급] {date} — 수집 {fetched}건 중 신규 {saved}건 저장 (중복 URL 제외)", date, items.Count, saved);
            }

            _logger.LogInformation("[뉴스 소급] 완료 — 총 신규 {total}건 저장", totalSaved);
        }

        /// <summary>
        /// --extract-keywords 1회성 모드 — 오늘(KST 00:00~현재) RawNews에서 급상승 키워드를
        /// 통계 + 로컬 Gemma로 뽑아 NewsKeywords에 저장한다. (블로그 소재용. 나중에 시간당 스케줄에 붙일 수 있음)
        /// </summary>
        private async Task RunKeywordExtractionAsync(CancellationToken ct)
        {
            var databasePath = ResolveDatabasePath();
            await InitializeDatabaseAsync(databasePath, ct);

            var nowKst = GetKoreaNow();
            var end = new DateTimeOffset(nowKst, TimeSpan.FromHours(9));
            var start = new DateTimeOffset(nowKst.Date, TimeSpan.FromHours(9)); // 오늘 00:00 KST

            _logger.LogInformation("[키워드] 추출 시작 — 대상 {s} ~ {e} (오늘 크롤링한 뉴스)", start, end);
            var count = await _keywordExtractor.ExtractAndSaveAsync(databasePath, start, end, baselineDays: 7, topCandidates: 120, ct);
            _logger.LogInformation("[키워드] 완료 — NewsKeywords에 {n}개 저장", count);
        }

        private static async Task InitializeDatabaseAsync(string databasePath, CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(cancellationToken);

            // 외래 키 체크를 일시적으로 끄고 테이블 생성
            var pragmaOff = connection.CreateCommand();
            pragmaOff.CommandText = "PRAGMA foreign_keys = OFF;";
            await pragmaOff.ExecuteNonQueryAsync(cancellationToken);

            var command = connection.CreateCommand();
            command.CommandText =
                """
                -- 1. RawNews 테이블
                CREATE TABLE IF NOT EXISTS RawNews (
                    Id TEXT NOT NULL PRIMARY KEY,
                    Title TEXT NOT NULL,
                    Summary TEXT NOT NULL,
                    Content TEXT NULL,
                    Url TEXT NOT NULL UNIQUE,
                    PublishedAt TEXT NOT NULL,
                    Source TEXT NOT NULL,
                    Category TEXT NULL,
                    Country TEXT NOT NULL,
                    ImageUrl TEXT NULL,
                    RawJson TEXT NOT NULL,
                    ReqPrompt TEXT NULL,
                    ResPrompt TEXT NULL,
                    CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UpdatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                CREATE INDEX IF NOT EXISTS IX_RawNews_PublishedAt ON RawNews(PublishedAt DESC);

                -- 3. Content 컬럼 마이그레이션 (기존 테이블 대응)
                BEGIN;
                -- SQLite는 IF NOT EXISTS COLUMN이 없으므로 정보를 조회하여 처리하거나 단순 오류 무시 방식을 사용
                -- 여기서는 가장 안전하게 PRAGMA를 사용하여 체크 후 실행하도록 하겠습니다.
                COMMIT;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);

            // [마이그레이션] Content 컬럼 추가 체크
            var checkColCmd = connection.CreateCommand();
            checkColCmd.CommandText = "PRAGMA table_info(RawNews);";
            bool contentExists = false;
            using (var reader = await checkColCmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (reader.GetString(1).Equals("Content", StringComparison.OrdinalIgnoreCase))
                    {
                        contentExists = true;
                        break;
                    }
                }
            }

            if (!contentExists)
            {
                var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE RawNews ADD COLUMN Content TEXT NULL;";
                await alterCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var categoryCmd = connection.CreateCommand();
            categoryCmd.CommandText = 
                """
                -- 2. Categories 테이블 (계층형 코드 체계)
                CREATE TABLE IF NOT EXISTS Categories (
                    CategoryCode TEXT NOT NULL PRIMARY KEY,
                    CategoryName TEXT NOT NULL,
                    Level INTEGER NOT NULL,                 -- 1(대), 2(중), 3(소), 4(세), 5(상세)
                    UpperCategoryCode TEXT,                 -- 상위 카테고리 코드
                    FullPath TEXT NULL,
                    CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    FOREIGN KEY(UpperCategoryCode) REFERENCES Categories(CategoryCode)
                );

                -- 3. NewsCategoryMapping 테이블 (N:M 매핑)
                CREATE TABLE IF NOT EXISTS NewsCategoryMapping (
                    NewsId TEXT NOT NULL,
                    CategoryCode TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    PRIMARY KEY(NewsId, CategoryCode),
                    FOREIGN KEY(NewsId) REFERENCES RawNews(Id) ON DELETE CASCADE,
                    FOREIGN KEY(CategoryCode) REFERENCES Categories(CategoryCode) ON DELETE CASCADE
                );

                -- 4. SummarizedNews 테이블
                CREATE TABLE IF NOT EXISTS SummarizedNews (
                    Id TEXT NOT NULL PRIMARY KEY,
                    Title TEXT NOT NULL,
                    Content TEXT NOT NULL,
                    ImageUrl TEXT NULL,
                    Category TEXT NOT NULL,
                    PublishedAt TEXT NOT NULL,
                    RelatedUrlsJson TEXT NOT NULL,
                    RelatedArticlesJson TEXT NOT NULL DEFAULT '[]',
                    SummaryJson TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                CREATE INDEX IF NOT EXISTS IX_SummarizedNews_PublishedAt ON SummarizedNews(PublishedAt DESC);

                -- 6. NewsSummaryMapping 테이블
                CREATE TABLE IF NOT EXISTS NewsSummaryMapping (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    NewsId TEXT NOT NULL,
                    Level_1 TEXT,
                    Level_2 TEXT,
                    Level_3 TEXT,
                    Level_4 TEXT,
                    Level_5 TEXT,
                    Summary TEXT,
                    CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    FOREIGN KEY(NewsId) REFERENCES RawNews(Id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS IX_NewsSummaryMapping_NewsId ON NewsSummaryMapping(NewsId);

                -- 7. KBO 공식 사이트 팀 순위 (수집 시점별 스냅샷)
                CREATE TABLE IF NOT EXISTS KboTeamStandings (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    TeamName TEXT NOT NULL,
                    Rank INTEGER NOT NULL,
                    Games INTEGER,
                    Wins INTEGER,
                    Losses INTEGER,
                    Draws INTEGER,
                    WinRate REAL,
                    GamesBehind TEXT,
                    Avg REAL,
                    Obp REAL,
                    Era REAL,
                    Oavg REAL,
                    CollectedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                -- 8. KBO 공식 사이트 타자 기록 (수집 시점별 스냅샷)
                CREATE TABLE IF NOT EXISTS KboBatterStats (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    PlayerName TEXT NOT NULL,
                    Team TEXT,
                    Avg REAL,
                    Games INTEGER,
                    Hits INTEGER,
                    HomeRuns INTEGER,
                    Rbi INTEGER,
                    Obp REAL,
                    AtBats INTEGER,
                    Runs INTEGER,
                    Doubles INTEGER,
                    Triples INTEGER,
                    StolenBases INTEGER,
                    Walks INTEGER,
                    Hbp INTEGER,
                    Strikeouts INTEGER,
                    CollectedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                -- 9. KBO 공식 사이트 투수 기록 (수집 시점별 스냅샷)
                CREATE TABLE IF NOT EXISTS KboPitcherStats (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    PlayerName TEXT NOT NULL,
                    Team TEXT,
                    Era REAL,
                    Wins INTEGER,
                    Losses INTEGER,
                    Saves INTEGER,
                    Innings TEXT,
                    Strikeouts INTEGER,
                    Oavg REAL,
                    HomeRuns INTEGER,
                    Games INTEGER,
                    Holds INTEGER,
                    HitsAllowed INTEGER,
                    RunsAllowed INTEGER,
                    EarnedRuns INTEGER,
                    Walks INTEGER,
                    Hbp INTEGER,
                    WinRate REAL,
                    InningsDecimal REAL,
                    CollectedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                -- 10. KBO 경기 결과 (날짜별, 중복 방지)
                CREATE TABLE IF NOT EXISTS KboGameResults (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    GameDate TEXT NOT NULL,
                    GameTime TEXT,
                    AwayTeam TEXT NOT NULL,
                    AwayScore INTEGER,
                    HomeTeam TEXT NOT NULL,
                    HomeScore INTEGER,
                    GameId TEXT,
                    CollectedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE(GameDate, AwayTeam, HomeTeam)
                );

                CREATE INDEX IF NOT EXISTS IX_KboGameResults_GameDate ON KboGameResults(GameDate DESC);

                -- 12. 경기별 박스스코어 (타자/투수 개인 기록, 종료된 경기만 1회 수집)
                CREATE TABLE IF NOT EXISTS KboBoxScoreBatting (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    GameId TEXT NOT NULL,
                    GameDate TEXT NOT NULL,
                    Team TEXT NOT NULL,
                    PlayerName TEXT NOT NULL,
                    AtBats INTEGER,
                    Runs INTEGER,
                    Hits INTEGER,
                    Rbi INTEGER,
                    HomeRuns INTEGER,
                    Walks INTEGER,
                    Hbp INTEGER,
                    SacFly INTEGER,
                    Doubles INTEGER,
                    Triples INTEGER,
                    Strikeouts INTEGER,
                    StolenBases INTEGER,
                    CollectedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE(GameId, PlayerName)
                );

                CREATE INDEX IF NOT EXISTS IX_KboBoxScoreBatting_Player ON KboBoxScoreBatting(PlayerName, GameDate DESC);

                CREATE TABLE IF NOT EXISTS KboBoxScorePitching (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    GameId TEXT NOT NULL,
                    GameDate TEXT NOT NULL,
                    Team TEXT NOT NULL,
                    PlayerName TEXT NOT NULL,
                    InningsPitched TEXT,
                    Hits INTEGER,
                    HomeRuns INTEGER,
                    Walks INTEGER,
                    Strikeouts INTEGER,
                    Runs INTEGER,
                    EarnedRuns INTEGER,
                    Decision TEXT,
                    CollectedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE(GameId, PlayerName)
                );

                CREATE INDEX IF NOT EXISTS IX_KboBoxScorePitching_Player ON KboBoxScorePitching(PlayerName, GameDate DESC);

                -- 11. 날짜별 KBO 하이라이트 (Gemma가 원문을 재작성한 요약, 저작권 대응)
                CREATE TABLE IF NOT EXISTS KboDateHighlights (
                    GameDate TEXT NOT NULL PRIMARY KEY,
                    HighlightText TEXT NOT NULL,
                    SourceCount INTEGER NOT NULL DEFAULT 0,
                    CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UpdatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                -- 13. 시간대별 뉴스 키워드 (RawNews에서 급상승 통계 + Gemma 정제로 뽑은 블로그 소재 키워드)
                CREATE TABLE IF NOT EXISTS NewsKeywords (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Keyword TEXT NOT NULL,              -- 정제된 대표 키워드 (블로그 소재)
                    Category TEXT,                      -- 정치/경제/사회/IT/연예/스포츠/일반 등
                    Gist TEXT,                          -- 한 줄 요지 (이 키워드가 왜 소재인지)
                    Score REAL,                         -- 통계 종합 점수 (빈도×급상승)
                    Frequency INTEGER,                  -- 대상 기간 등장 기사 수
                    SpikeRatio REAL,                    -- 평소(기준선) 대비 급상승 배수
                    SearchVolume REAL,                  -- (C단계) 네이버 검색량/상승세, 없으면 NULL
                    RelatedNewsIds TEXT,                -- JSON 배열: 관련 RawNews.Id
                    SampleTitles TEXT,                  -- JSON 배열: 대표 기사 제목
                    Method TEXT NOT NULL DEFAULT 'gemma-hybrid',  -- 생성 방식 구분
                    WindowStart TEXT,                   -- 집계 대상 기간 시작 (ISO)
                    WindowEnd TEXT,                     -- 집계 대상 기간 끝 (ISO)
                    CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                CREATE INDEX IF NOT EXISTS IX_NewsKeywords_Created ON NewsKeywords(CreatedAt DESC);
                CREATE INDEX IF NOT EXISTS IX_NewsKeywords_Category ON NewsKeywords(Category, CreatedAt DESC);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await categoryCmd.ExecuteNonQueryAsync(cancellationToken);

            // 외래 키 체크 다시 켜기
            var pragmaOn = connection.CreateCommand();
            pragmaOn.CommandText = "PRAGMA foreign_keys = ON;";
            await pragmaOn.ExecuteNonQueryAsync(cancellationToken);

            // 마이그레이션: SummarizedNews에 RelatedArticlesJson 컬럼이 없는 경우 추가
            await AddColumnIfNotExistAsync(connection, "SummarizedNews", "RelatedArticlesJson", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "Categories", "FullPath", "TEXT NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "RawNews", "ReqPrompt", "TEXT NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "RawNews", "ResPrompt", "TEXT NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboBatterStats", "Obp", "REAL NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboPitcherStats", "Oavg", "REAL NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboPitcherStats", "HomeRuns", "INTEGER NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboBoxScoreBatting", "Walks", "INTEGER NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboBoxScoreBatting", "Hbp", "INTEGER NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboBoxScoreBatting", "SacFly", "INTEGER NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboBoxScoreBatting", "Doubles", "INTEGER NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboBoxScoreBatting", "Triples", "INTEGER NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboBoxScoreBatting", "Strikeouts", "INTEGER NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboBoxScoreBatting", "StolenBases", "INTEGER NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboBatterStats", "AtBats", "INTEGER NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboBatterStats", "Runs", "INTEGER NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboBatterStats", "Doubles", "INTEGER NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboBatterStats", "Triples", "INTEGER NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboBatterStats", "StolenBases", "INTEGER NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboBatterStats", "Walks", "INTEGER NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboBatterStats", "Hbp", "INTEGER NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboBatterStats", "Strikeouts", "INTEGER NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboBoxScorePitching", "Decision", "TEXT NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboPitcherStats", "Games", "INTEGER NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboPitcherStats", "Holds", "INTEGER NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboPitcherStats", "HitsAllowed", "INTEGER NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboPitcherStats", "RunsAllowed", "INTEGER NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboPitcherStats", "EarnedRuns", "INTEGER NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboPitcherStats", "Walks", "INTEGER NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboPitcherStats", "Hbp", "INTEGER NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboPitcherStats", "WinRate", "REAL NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboPitcherStats", "InningsDecimal", "REAL NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboTeamStandings", "Avg", "REAL NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboTeamStandings", "Obp", "REAL NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboTeamStandings", "Era", "REAL NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboTeamStandings", "Oavg", "REAL NULL", cancellationToken);
            await AddColumnIfNotExistAsync(connection, "KboGameResults", "GameId", "TEXT NULL", cancellationToken);
            await BackfillCategoryFullPathsAsync(connection, cancellationToken);
            await SeedInitialCategoriesAsync(connection, cancellationToken);
        }

        private static async Task SeedInitialCategoriesAsync(SqliteConnection connection, CancellationToken ct)
        {
            var initialCategories = new[] { "정치", "경제", "사회", "IT", "과학", "스포츠", "연예" };
            
            await using var transaction = await connection.BeginTransactionAsync(ct);
            
            for (int i = 0; i < initialCategories.Length; i++)
            {
                var name = initialCategories[i];
                var code = $"FL_{(i + 1):D3}";
                
                var cmd = connection.CreateCommand();
                cmd.Transaction = (SqliteTransaction)transaction;
                cmd.CommandText = 
                    """
                    INSERT OR IGNORE INTO Categories (CategoryCode, CategoryName, Level, FullPath) 
                    VALUES ($code, $name, 1, $name);
                    """;
                cmd.Parameters.AddWithValue("$code", code);
                cmd.Parameters.AddWithValue("$name", name);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }

        private static async Task AddColumnIfNotExistAsync(SqliteConnection connection, string tableName, string columnName, string columnDefinition, CancellationToken cancellationToken)
        {
            var checkCommand = connection.CreateCommand();
            checkCommand.CommandText = $"PRAGMA table_info({tableName});";
            var exists = false;
            using (var reader = await checkCommand.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (reader.GetString(1).Equals(columnName, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
            }

            if (!exists)
            {
                var alterCommand = connection.CreateCommand();
                alterCommand.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
                await alterCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        private static async Task BackfillCategoryFullPathsAsync(SqliteConnection connection, CancellationToken cancellationToken)
        {
            var categories = new List<(string Code, string Name, string? ParentCode)>();
            var selectCommand = connection.CreateCommand();
            selectCommand.CommandText =
                """
                SELECT CategoryCode, CategoryName, UpperCategoryCode
                FROM Categories;
                """;

            await using (var reader = await selectCommand.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    categories.Add((
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2)));
                }
            }

            if (categories.Count == 0)
            {
                return;
            }

            var byCode = categories.ToDictionary(category => category.Code, StringComparer.OrdinalIgnoreCase);
            var fullPathCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string BuildFullPath(string code)
            {
                if (fullPathCache.TryGetValue(code, out var cached))
                {
                    return cached;
                }

                var category = byCode[code];
                var fullPath = !string.IsNullOrWhiteSpace(category.ParentCode) && byCode.ContainsKey(category.ParentCode)
                    ? $"{BuildFullPath(category.ParentCode)} > {category.Name}"
                    : category.Name;

                fullPathCache[code] = fullPath;
                return fullPath;
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            foreach (var category in categories)
            {
                var updateCommand = connection.CreateCommand();
                updateCommand.Transaction = (SqliteTransaction)transaction;
                updateCommand.CommandText =
                    """
                    UPDATE Categories
                    SET FullPath = $fullPath
                    WHERE CategoryCode = $code
                      AND (FullPath IS NULL OR FullPath = '');
                    """;
                updateCommand.Parameters.AddWithValue("$fullPath", BuildFullPath(category.Code));
                updateCommand.Parameters.AddWithValue("$code", category.Code);
                await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        private static async Task<int> GetNextTickAsync(string databasePath, string dateStr, CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(cancellationToken);

            var tickCommand = connection.CreateCommand();
            tickCommand.CommandText = "SELECT Id FROM RawNews WHERE Id LIKE $pattern ORDER BY Id DESC LIMIT 1;";
            tickCommand.Parameters.AddWithValue("$pattern", $"News_{dateStr}_%");
            var lastId = await tickCommand.ExecuteScalarAsync(cancellationToken) as string;

            if (string.IsNullOrEmpty(lastId))
            {
                return 1;
            }

            var parts = lastId.Split('_');
            if (parts.Length >= 3 && int.TryParse(parts[2], out int lastTick))
            {
                return lastTick + 1;
            }

            return 1;
        }

        private static async Task<int> SaveRawNewsAsync(
            string databasePath,
            IEnumerable<NewsItem> items,
            string dateStr,
            string tickStr,
            CancellationToken cancellationToken)
        {
            var saved = 0;

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(cancellationToken);

            var distinctItems = items
                .Where(i => !string.IsNullOrWhiteSpace(i.Url))
                .GroupBy(i => i.Url, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (distinctItems.Count == 0) return 0;

            var urlsToCheck = distinctItems.Select(i => i.Url).ToList();
            var existingUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var checkCommand = connection.CreateCommand();
            var placeholders = string.Join(",", urlsToCheck.Select((_, i) => $"$url{i}"));
            checkCommand.CommandText = $"SELECT Url FROM RawNews WHERE Url IN ({placeholders});";
            for (int i = 0; i < urlsToCheck.Count; i++)
            {
                checkCommand.Parameters.AddWithValue($"$url{i}", urlsToCheck[i]);
            }

            await using (var reader = await checkCommand.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    existingUrls.Add(reader.GetString(0));
                }
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var index = 1;

            foreach (var item in distinctItems)
            {
                if (existingUrls.Contains(item.Url))
                {
                    continue;
                }

                var customId = $"News_{dateStr}_{tickStr}_{index++}";

                var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText =
                    """
                    INSERT OR IGNORE INTO RawNews
                        (Id, Title, Summary, Content, Url, PublishedAt, Source, Category, Country, ImageUrl, RawJson)
                    VALUES
                        ($id, $title, $summary, $content, $url, $publishedAt, $source, $category, $country, $imageUrl, $rawJson);
                    """;

                command.Parameters.AddWithValue("$id", customId);
                command.Parameters.AddWithValue("$title", item.Title);
                command.Parameters.AddWithValue("$summary", item.Summary);
                command.Parameters.AddWithValue("$content", (object?)item.Content ?? DBNull.Value);
                command.Parameters.AddWithValue("$url", item.Url);
                command.Parameters.AddWithValue("$publishedAt", item.PublishedAt.ToString("O"));
                command.Parameters.AddWithValue("$source", item.Source);
                command.Parameters.AddWithValue("$category", item.Category);
                command.Parameters.AddWithValue("$country", item.Country);
                command.Parameters.AddWithValue("$imageUrl", (object?)item.ImageUrl ?? DBNull.Value);
                command.Parameters.AddWithValue("$rawJson", JsonSerializer.Serialize(item, JsonOptions));

                var result = await command.ExecuteNonQueryAsync(cancellationToken);
                if (result > 0)
                {
                    saved++;
                    existingUrls.Add(item.Url);
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return saved;
        }

        private static async Task<List<NewsItem>> LoadRawNewsAsync(string databasePath, CancellationToken cancellationToken)
        {
            var limit = 200;
            var items = new List<NewsItem>();

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(cancellationToken);

            var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT RawJson
                FROM RawNews
                ORDER BY PublishedAt DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var rawJson = reader.GetString(0);
                var item = JsonSerializer.Deserialize<NewsItem>(rawJson, JsonOptions);
                if (item is not null)
                {
                    items.Add(item);
                }
            }

            return items;
        }

        private static async Task ReplaceSummarizedNewsAsync(
            string databasePath,
            IEnumerable<Albatross.Shared.Models.NewsItem> items,
            string dateStr,
            string tickStr,
            CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(cancellationToken);

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var backupCommand = connection.CreateCommand();
            backupCommand.Transaction = (SqliteTransaction)transaction;
            backupCommand.CommandText =
                """
                INSERT INTO SummarizedNewsHist
                    (Id, Title, Content, ImageUrl, Category, PublishedAt, RelatedUrlsJson, RelatedArticlesJson, SummaryJson, CreatedAt)
                SELECT
                    Id, Title, Content, ImageUrl, Category, PublishedAt, RelatedUrlsJson, RelatedArticlesJson, SummaryJson, CreatedAt
                FROM SummarizedNews;
                """;
            await backupCommand.ExecuteNonQueryAsync(cancellationToken);

            var deleteCommand = connection.CreateCommand();
            deleteCommand.Transaction = (SqliteTransaction)transaction;
            deleteCommand.CommandText = "DELETE FROM SummarizedNews;";
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);

            var index = 1;
            foreach (var item in items)
            {
                item.Id = $"Sum_{dateStr}_{tickStr}_{index++}";

                var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText =
                    """
                    INSERT OR REPLACE INTO SummarizedNews
                        (Id, Title, Content, ImageUrl, Category, PublishedAt, RelatedUrlsJson, RelatedArticlesJson, SummaryJson)
                    VALUES
                        ($id, $title, $content, $imageUrl, $category, $publishedAt, $relatedUrlsJson, $relatedArticlesJson, $summaryJson);
                    """;

                command.Parameters.AddWithValue("$id", item.Id);
                command.Parameters.AddWithValue("$title", item.Title);
                command.Parameters.AddWithValue("$content", item.Content);
                command.Parameters.AddWithValue("$imageUrl", (object?)item.ImageUrl ?? DBNull.Value);
                command.Parameters.AddWithValue("$category", item.Category);
                command.Parameters.AddWithValue("$publishedAt", item.PublishedAt);
                command.Parameters.AddWithValue("$relatedUrlsJson", JsonSerializer.Serialize(item.RelatedUrls, JsonOptions));
                command.Parameters.AddWithValue("$relatedArticlesJson", JsonSerializer.Serialize(item.RelatedArticles, JsonOptions));
                command.Parameters.AddWithValue("$summaryJson", JsonSerializer.Serialize(item, JsonOptions));

                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        private static async Task ExportSummarizedNewsToJsonAsync(
            string databasePath,
            string outPath,
            CancellationToken cancellationToken)
        {
            var items = new List<Albatross.Shared.Models.NewsItem>();

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(cancellationToken);

            var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT SummaryJson
                FROM SummarizedNews
                ORDER BY datetime(PublishedAt) DESC;
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var summaryJson = reader.GetString(0);
                var item = JsonSerializer.Deserialize<Albatross.Shared.Models.NewsItem>(summaryJson, JsonOptions);
                if (item is not null)
                {
                    items.Add(item);
                }
            }

            var jsonString = JsonSerializer.Serialize(items, JsonOptions);
            await File.WriteAllTextAsync(outPath, jsonString, cancellationToken);
        }

        private async Task<string> EnsureCategoryHierarchyAsync(string databasePath, string categoryPath, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(categoryPath)) return "Unknown";

            var parts = categoryPath.Split('>').Select(p => p.Trim()).Where(p => !string.IsNullOrEmpty(p)).ToList();
            string? parentCode = null;
            string currentFullPath = "";

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(ct);

            for (int i = 0; i < parts.Count; i++)
            {
                int level = i + 1;
                string name = parts[i];
                currentFullPath = string.IsNullOrEmpty(currentFullPath) ? name : $"{currentFullPath} > {name}";

                var checkCmd = connection.CreateCommand();
                checkCmd.CommandText = "SELECT CategoryCode FROM Categories WHERE FullPath = $path;";
                checkCmd.Parameters.AddWithValue("$path", currentFullPath);
                var existingCode = await checkCmd.ExecuteScalarAsync(ct) as string;

                if (existingCode != null)
                {
                    parentCode = existingCode;
                    continue;
                }

                string prefix = level switch
                {
                    1 => "FL",
                    2 => "S2L",
                    3 => "S3L",
                    4 => "S4L",
                    _ => $"L{level}"
                };

                var countCmd = connection.CreateCommand();
                countCmd.CommandText = "SELECT COUNT(*) FROM Categories WHERE Level = $level;";
                countCmd.Parameters.AddWithValue("$level", level);
                long currentCount = (long)(await countCmd.ExecuteScalarAsync(ct) ?? 0L);
                string newCode = $"{prefix}_{(currentCount + 1):D3}";

                var insertCmd = connection.CreateCommand();
                insertCmd.CommandText = 
                    """
                    INSERT INTO Categories (CategoryCode, CategoryName, Level, UpperCategoryCode, FullPath) 
                    VALUES ($code, $name, $level, $upper, $path);
                    """;
                insertCmd.Parameters.AddWithValue("$code", newCode);
                insertCmd.Parameters.AddWithValue("$name", name);
                insertCmd.Parameters.AddWithValue("$level", level);
                insertCmd.Parameters.AddWithValue("$upper", (object?)parentCode ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("$path", currentFullPath);
                await insertCmd.ExecuteNonQueryAsync(ct);

                parentCode = newCode;
            }

            return parentCode ?? "Unknown";
        }

        private async Task MapNewsToCategoryAsync(string databasePath, string newsId, string categoryCode, CancellationToken ct)
        {
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(ct);

            var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO NewsCategoryMapping (NewsId, CategoryCode) VALUES ($newsId, $code);";
            cmd.Parameters.AddWithValue("$newsId", newsId);
            cmd.Parameters.AddWithValue("$code", categoryCode);
            
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private async Task<string> CallAiWithFallbackAsync(string prompt, string responseFormat, CancellationToken ct)
        {
            var groqApiKey = _config["GROQ_API_KEY"];
            var groqModel = _config["Groq:Model"] ?? "llama-3.3-70b-versatile";
            var groqApiUrl = "https://api.groq.com/openai/v1/chat/completions";

            if (!string.IsNullOrEmpty(groqApiKey))
            {
                var response = await CallGroqApiAsync(groqApiUrl, groqApiKey, groqModel, prompt, ct);
                if (!string.IsNullOrWhiteSpace(response))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(response);
                        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                        if (!string.IsNullOrWhiteSpace(content)) return content;
                    }
                    catch { }
                }
            }

            _logger.LogWarning("Groq API is unavailable or limit exceeded. Falling back to Gemini API...");
            
            var geminiApiKey = _config["GEMINI_API_KEY"] ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");
            var geminiModel = _config["Gemini:Model"] ?? "gemini-1.5-flash";
            
            if (string.IsNullOrEmpty(geminiApiKey))
            {
                _logger.LogError("Fallback failed: GEMINI_API_KEY is not configured.");
                return string.Empty;
            }

            var geminiRes = await CallGeminiApiAsync(geminiApiKey, geminiModel, prompt, responseFormat, ct);
            return geminiRes ?? string.Empty;
        }

        private async Task<Dictionary<string, string>> GetCategorySummariesAsync(string databasePath, List<NewsItem> items, CancellationToken ct)
        {
            var summaries = new Dictionary<string, string>();
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(ct);

            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Level_1 || '/' || Level_2 || '/' || Level_3 AS Category, Summary FROM NewsSummaryMapping;";

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var category = reader.GetString(0);
                var summary = reader.GetString(1);
                summaries[category] = summary;
            }
            return summaries;
        }

        private async Task UpdateSummarizedNewsWithBackupAsync(string databasePath, string category, string jsonSummary, CancellationToken ct)
        {
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);

            try
            {
                // 기존 데이터 백업
                var backupCmd = connection.CreateCommand();
                backupCmd.Transaction = (SqliteTransaction)transaction;
                backupCmd.CommandText = "INSERT INTO SummarizedNewsHist (Category, Summary) SELECT Category, Summary FROM SummarizedNews WHERE Category = $cat;";
                backupCmd.Parameters.AddWithValue("$cat", category);
                await backupCmd.ExecuteNonQueryAsync(ct);

                // 기존 데이터 삭제
                var deleteCmd = connection.CreateCommand();
                deleteCmd.Transaction = (SqliteTransaction)transaction;
                deleteCmd.CommandText = "DELETE FROM SummarizedNews WHERE Category = $cat;";
                deleteCmd.Parameters.AddWithValue("$cat", category);
                await deleteCmd.ExecuteNonQueryAsync(ct);

                // 신규 데이터 삽입
                var insertCmd = connection.CreateCommand();
                insertCmd.Transaction = (SqliteTransaction)transaction;
                insertCmd.CommandText = "INSERT INTO SummarizedNews (Category, Summary) VALUES ($cat, $sum);";
                insertCmd.Parameters.AddWithValue("$cat", category);
                insertCmd.Parameters.AddWithValue("$sum", jsonSummary);
                await insertCmd.ExecuteNonQueryAsync(ct);

                await transaction.CommitAsync(ct);
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
        }

        private async Task<List<NewsItem>> LoadRawNewsForSummaryAsync(string databasePath, CancellationToken ct)
        {
            // 실제 구현: 미분류 또는 최근 분석된 뉴스 로드
            return new List<NewsItem>(); 
        }

        private async Task SavePhase3SummariesAsync(string databasePath, IEnumerable<Albatross.Shared.Models.NewsItem> summaries, CancellationToken ct)
        {
             // 실제 저장 로직
        }

        private async Task<string> CallGemmaJsonAsync(string prompt, CancellationToken ct)

        {
            var endpoint = _config["Gemma:Endpoint"] ?? "http://localhost:11434/api/generate";
            var model = _config["Gemma:Model"] ?? "gemma4:e4b";
            var numPredict = _config.GetValue<int>("Gemma:NumPredict", 8192);
            var numCtx = _config.GetValue<int>("Gemma:NumCtx", 8192);

            try
            {
                for (var attempt = 1; attempt <= 2; attempt++)
                {
                    var promptToSend = attempt == 1
                        ? prompt
                        : $"""
                           The previous response was invalid or incomplete JSON.
                           Return exactly one complete JSON object only. Close every object and array. No markdown.

                           {prompt}
                           """;

                    var requestBody = new
                    {
                        model,
                        prompt = promptToSend,
                        stream = false,
                        format = "json",
                        options = new
                        {
                            temperature = 0.1,
                            num_predict = numPredict,
                            num_ctx = numCtx
                        }
                    };

                    var jsonRequest = JsonSerializer.Serialize(requestBody, JsonOptions);
                    using var requestContent = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

                    var stopwatch = Stopwatch.StartNew();
                    var response = await _httpClient.PostAsync(endpoint, requestContent, ct);
                    stopwatch.Stop();
                    var responseContent = await response.Content.ReadAsStringAsync(ct);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogError("Local Gemma API Error: {status} - {msg}", response.StatusCode, responseContent);
                        return string.Empty;
                    }

                    using var doc = JsonDocument.Parse(responseContent);
                    var resultText = doc.RootElement.TryGetProperty("response", out var responseElement)
                        ? responseElement.GetString()?.Trim()
                        : null;

                    if (string.IsNullOrWhiteSpace(resultText))
                    {
                        _logger.LogWarning("Local Gemma returned an empty response. Raw: {response}", responseContent);
                        continue;
                    }

                    var normalizedJson = TryNormalizeJsonObject(resultText);
                    if (!string.IsNullOrWhiteSpace(normalizedJson))
                    {
                        _logger.LogInformation(
                            "Local Gemma JSON call succeeded. Model: {model}. Attempt: {attempt}. Elapsed: {ms}ms",
                            model,
                            attempt,
                            stopwatch.ElapsedMilliseconds);
                        return normalizedJson;
                    }

                    _logger.LogWarning(
                        "Local Gemma returned invalid JSON. Model: {model}. Attempt: {attempt}. Preview: {preview}",
                        model,
                        attempt,
                        TruncateForLog(resultText, 500));
                }

                return string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling local Gemma API.");
                return string.Empty;
            }
        }

        private static string? TryNormalizeJsonObject(string value)
        {
            var trimmed = value.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                trimmed = string.Join(
                    Environment.NewLine,
                    trimmed.Split('\n').Where(line => !line.TrimStart().StartsWith("```", StringComparison.Ordinal)))
                    .Trim();
            }

            var firstBrace = trimmed.IndexOf('{');
            var lastBrace = trimmed.LastIndexOf('}');
            if (firstBrace < 0 || lastBrace <= firstBrace)
            {
                return null;
            }

            var candidate = trimmed[firstBrace..(lastBrace + 1)];
            try
            {
                using var _ = JsonDocument.Parse(candidate);
                return candidate;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string TruncateForLog(string value, int maxLength)
        {
            return value.Length <= maxLength ? value : value[..maxLength] + "...";
        }

        private async Task<string?> CallGeminiApiAsync(string apiKey, string model, string prompt, string responseMimeType, CancellationToken ct)
        {
            var apiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
            
            var requestBody = new
            {
                contents = new[] { new { parts = new[] { new { text = prompt } } } },
                generationConfig = new { responseMimeType = responseMimeType }
            };

            var jsonRequest = JsonSerializer.Serialize(requestBody, JsonOptions);
            using var requestContent = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

            try
            {
                var stopwatch = Stopwatch.StartNew();
                var response = await _httpClient.PostAsync(apiUrl, requestContent, ct);
                stopwatch.Stop();
                var responseContent = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Gemini API Error: {status} - {msg}", response.StatusCode, responseContent);
                    return null;
                }

                using var doc = JsonDocument.Parse(responseContent);
                var text = doc.RootElement
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text")
                    .GetString();

                _logger.LogInformation("Gemini Call Succeeded ({ms}ms)", stopwatch.ElapsedMilliseconds);
                return text;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling Gemini API.");
                return null;
            }
        }

        private async Task<IEnumerable<Albatross.Shared.Models.NewsItem>> AnalyzeNewsWithAI(string databasePath, IEnumerable<NewsItem> items, CancellationToken stoppingToken)
        {
            var sourceItems = items.ToList();
            if (sourceItems.Count == 0) return Enumerable.Empty<Albatross.Shared.Models.NewsItem>();

            // 1. 신규 카테고리 분석된 뉴스들만 대상 (데이터베이스에서 필터링된 리스트를 이미 받았다고 가정)
            
            // 2. 같은 카테고리의 요약된 내용(NewsSummaryMapping) 조회
            var categorySummaries = await GetCategorySummariesAsync(databasePath, sourceItems, stoppingToken);

            var finalItems = new List<Albatross.Shared.Models.NewsItem>();
            var groupedItems = sourceItems.GroupBy(item => item.Category);

            foreach (var group in groupedItems)
            {
                var category = group.Key;
                var currentSummary = categorySummaries.GetValueOrDefault(category, "");

                var phase3Prompt = $$"""
                    당신은 뉴스 요약 전문가입니다. 아래 [뉴스 기사 그룹]과 [기존 카테고리 요약]을 분석하여, 
                    전체적인 흐름을 반영한 최신 종합 요약문을 생성해주세요.

                    [기존 카테고리 요약]
                    {{currentSummary}}

                    [뉴스 기사 그룹]
                    {{JsonSerializer.Serialize(group.Select(item => new { item.Title, item.Content }), new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })}}

                    반드시 아래의 깨끗한 JSON 객체 형식으로만 응답해 주세요.
                    {
                      "content": "..."
                    }
                    """;

                _logger.LogInformation("Phase 3 - Sending request to Gemma for category {category}...", category);
                _logger.LogInformation("Full Phase 3 Request Prompt:\n{Prompt}", phase3Prompt);

                var phase3Json = await _classifier.CallGemmaJsonAsync(phase3Prompt, stoppingToken);
                
                // 3. SummarizedNews 갱신 (변경 시 Hist 백업)
                await UpdateSummarizedNewsWithBackupAsync(databasePath, category, phase3Json, stoppingToken);

                // ... (결과 구성 로직)
                finalItems.Add(new Albatross.Shared.Models.NewsItem { /* ... */ });
            }
            return finalItems;
        }

        private async Task<(string? Code, string? FullPath)> GetExistingCategoryByNewsIdAsync(string databasePath, string newsId, CancellationToken ct)
        {
            try
            {
                await using var connection = new SqliteConnection($"Data Source={databasePath}");
                await connection.OpenAsync(ct);

                var cmd = connection.CreateCommand();
                cmd.CommandText = 
                    """
                    SELECT c.CategoryCode, c.FullPath 
                    FROM Categories c
                    JOIN NewsCategoryMapping m ON c.CategoryCode = m.CategoryCode
                    WHERE m.NewsId = $newsId
                    LIMIT 1;
                    """;
                cmd.Parameters.AddWithValue("$newsId", newsId);

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    var code = reader.IsDBNull(0) ? null : reader.GetString(0);
                    var fullPath = reader.IsDBNull(1) ? null : reader.GetString(1);
                    return (code, fullPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get existing category by NewsId {newsId}", newsId);
            }

            return (null, null);
        }

        private async Task UpdateAndRemapCategoryAsync(string databasePath, string oldCategoryCode, string newCategoryCode, string oldFullPath, string newFullPath, CancellationToken ct)
        {
            if (oldCategoryCode == newCategoryCode) return;

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            
            try
            {
                // 1. NewsCategoryMapping 테이블의 매핑 데이터를 일괄 변경 (구 코드 -> 신 코드)
                var updateMappingCmd = connection.CreateCommand();
                updateMappingCmd.Transaction = (SqliteTransaction)transaction;
                updateMappingCmd.CommandText = 
                    """
                    UPDATE OR IGNORE NewsCategoryMapping 
                    SET CategoryCode = $newCode 
                    WHERE CategoryCode = $oldCode;
                    """;
                updateMappingCmd.Parameters.AddWithValue("$newCode", newCategoryCode);
                updateMappingCmd.Parameters.AddWithValue("$oldCode", oldCategoryCode);
                await updateMappingCmd.ExecuteNonQueryAsync(ct);

                // 2. 다대다 중복 매핑이 발생하여 무시되고 잔여한 구 매핑 데이터 제거
                var deleteDupCmd = connection.CreateCommand();
                deleteDupCmd.Transaction = (SqliteTransaction)transaction;
                deleteDupCmd.CommandText = "DELETE FROM NewsCategoryMapping WHERE CategoryCode = $oldCode;";
                deleteDupCmd.Parameters.AddWithValue("$oldCode", oldCategoryCode);
                await deleteDupCmd.ExecuteNonQueryAsync(ct);

                // 3. SummarizedNews 테이블의 카테고리명 텍스트 필드 일괄 수정
                var updateSummarizedCmd = connection.CreateCommand();
                updateSummarizedCmd.Transaction = (SqliteTransaction)transaction;
                updateSummarizedCmd.CommandText = 
                    """
                    UPDATE SummarizedNews 
                    SET Category = $newPath 
                    WHERE Category = $oldPath;
                    """;
                updateSummarizedCmd.Parameters.AddWithValue("$newPath", newFullPath);
                updateSummarizedCmd.Parameters.AddWithValue("$oldPath", oldFullPath);
                await updateSummarizedCmd.ExecuteNonQueryAsync(ct);

                // 4. 참조되지 않는 구 고아(Orphan) 카테고리 데이터 안전 삭제
                var deleteOrphanCmd = connection.CreateCommand();
                deleteOrphanCmd.Transaction = (SqliteTransaction)transaction;
                deleteOrphanCmd.CommandText = 
                    """
                    DELETE FROM Categories 
                    WHERE CategoryCode = $oldCode 
                      AND NOT EXISTS (SELECT 1 FROM NewsCategoryMapping WHERE CategoryCode = $oldCode);
                    """;
                deleteOrphanCmd.Parameters.AddWithValue("$oldCode", oldCategoryCode);
                await deleteOrphanCmd.ExecuteNonQueryAsync(ct);

                await transaction.CommitAsync(ct);
                _logger.LogInformation("Successfully remapped all historical news from category [{oldPath}] to [{newPath}].", oldFullPath, newFullPath);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(ct);
                _logger.LogError(ex, "Failed to remap categories from {oldCode} to {newCode}", oldCategoryCode, newCategoryCode);
            }
        }

        private async Task<string> CallGroqApiAsync(string apiUrl, string apiKey, string model, string prompt, CancellationToken stoppingToken)
        {
            var requestBody = new
            {
                model = model,
                messages = new[] { new { role = "user", content = prompt } },
                response_format = new { type = "json_object" },
                temperature = 0.2
            };

            var jsonRequest = JsonSerializer.Serialize(requestBody, JsonOptions);
            using var requestContent = new StringContent(jsonRequest, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl) { Content = requestContent };
            request.Headers.Add("Authorization", $"Bearer {apiKey}");

            try
            {
                var stopwatch = Stopwatch.StartNew();
                var response = await _httpClient.SendAsync(request, stoppingToken);
                stopwatch.Stop();
                var responseContent = await response.Content.ReadAsStringAsync(stoppingToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Groq API Error: {status} - {msg}", response.StatusCode, responseContent);
                    return string.Empty;
                }

                using var doc = JsonDocument.Parse(responseContent);
                if (doc.RootElement.TryGetProperty("usage", out var usage))
                {
                    _logger.LogInformation("Groq Usage - P: {p}, C: {c}, T: {t} ({ms}ms)", 
                        usage.GetProperty("prompt_tokens").GetInt32(), 
                        usage.GetProperty("completion_tokens").GetInt32(), 
                        usage.GetProperty("total_tokens").GetInt32(),
                        stopwatch.ElapsedMilliseconds);
                }

                return responseContent;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling Groq API.");
                return string.Empty;
            }
        }

        private async Task<IEnumerable<Albatross.Shared.Models.NewsItem>> DeserializeGeminiNewsAsync(
            string jsonText,
            string apiUrl,
            string model,
            int sourceItemCount,
            CancellationToken cancellationToken)
        {
            try
            {
                return JsonSerializer.Deserialize<IEnumerable<Albatross.Shared.Models.NewsItem>>(jsonText, JsonOptions)
                    ?? Enumerable.Empty<Albatross.Shared.Models.NewsItem>();
            }
            catch (JsonException ex)
            {
                var locallyRepairedJsonText = RepairInvalidJsonEscapes(jsonText);
                if (!string.Equals(jsonText, locallyRepairedJsonText, StringComparison.Ordinal))
                {
                    try
                    {
                        var locallyRepairedData = JsonSerializer.Deserialize<IEnumerable<Albatross.Shared.Models.NewsItem>>(locallyRepairedJsonText, JsonOptions)
                            ?? Enumerable.Empty<Albatross.Shared.Models.NewsItem>();

                        _logger.LogInformation("Parsed Gemini analysis JSON after repairing invalid escape sequences locally.");
                        return locallyRepairedData;
                    }
                    catch (JsonException localRepairEx)
                    {
                        var failedPath = await SaveFailedGeminiResponseAsync(jsonText, "gemini-analysis-json", cancellationToken);
                        _logger.LogError(ex, "Failed to parse Gemini analysis JSON. Raw response saved to: {path}", failedPath);

                        var localRepairFailedPath = await SaveFailedGeminiResponseAsync(locallyRepairedJsonText, "gemini-analysis-json-local-repair", cancellationToken);
                        _logger.LogWarning(localRepairEx, "Failed to parse locally repaired Gemini analysis JSON. Repaired response saved to: {path}", localRepairFailedPath);
                    }
                }
                else
                {
                    var failedPath = await SaveFailedGeminiResponseAsync(jsonText, "gemini-analysis-json", cancellationToken);
                    _logger.LogError(ex, "Failed to parse Gemini analysis JSON. Raw response saved to: {path}", failedPath);
                }

                var repairPrompt = $@"
The previous response was invalid JSON and failed to parse.
Return only a corrected, valid JSON array. Do not add markdown or explanations.
Do not use \u escapes. Write UTF-8 text directly.
If a string contains a backslash, escape it as \\.
Omit relatedArticles or return it as an empty array. Preserve each URL in relatedUrls.

[Invalid JSON to fix]
{jsonText}
";

                var repairedJsonText = await CallGeminiRepairAsync(apiUrl, model, sourceItemCount, repairPrompt, cancellationToken);
                if (string.IsNullOrWhiteSpace(repairedJsonText))
                {
                    return Enumerable.Empty<Albatross.Shared.Models.NewsItem>();
                }

                try
                {
                    return JsonSerializer.Deserialize<IEnumerable<Albatross.Shared.Models.NewsItem>>(repairedJsonText, JsonOptions)
                        ?? Enumerable.Empty<Albatross.Shared.Models.NewsItem>();
                }
                catch (JsonException retryEx)
                {
                    var retryFailedPath = await SaveFailedGeminiResponseAsync(repairedJsonText, "gemini-analysis-json-retry", cancellationToken);
                    _logger.LogError(retryEx, "Failed to parse repaired Gemini analysis JSON. Raw response saved to: {path}", retryFailedPath);
                    return Enumerable.Empty<Albatross.Shared.Models.NewsItem>();
                }
            }
        }

        private static string RepairInvalidJsonEscapes(string jsonText)
        {
            var repaired = new StringBuilder(jsonText.Length);

            for (var i = 0; i < jsonText.Length; i++)
            {
                var current = jsonText[i];
                if (current != '\\' || i == jsonText.Length - 1)
                {
                    repaired.Append(current);
                    continue;
                }

                var next = jsonText[i + 1];
                if (next is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't')
                {
                    repaired.Append(current);
                    repaired.Append(next);
                    i++;
                    continue;
                }

                if (next == 'u' && i + 5 < jsonText.Length
                    && IsHexDigit(jsonText[i + 2])
                    && IsHexDigit(jsonText[i + 3])
                    && IsHexDigit(jsonText[i + 4])
                    && IsHexDigit(jsonText[i + 5]))
                {
                    repaired.Append(current);
                    repaired.Append(jsonText, i + 1, 5);
                    i += 5;
                    continue;
                }

                repaired.Append(next);
                i++;
            }

            return repaired.ToString();
        }

        private static bool IsHexDigit(char value) =>
            value is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F';

        private async Task<string?> CallGeminiRepairAsync(
            string apiUrl,
            string model,
            int sourceItemCount,
            string prompt,
            CancellationToken cancellationToken)
        {
            var requestBody = new
            {
                contents = new[]
                {
                    new { parts = new[] { new { text = prompt } } }
                },
                generationConfig = new
                {
                    responseMimeType = "application/json"
                }
            };

            var jsonRequest = JsonSerializer.Serialize(requestBody, JsonOptions);
            using var requestContent = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

            _logger.LogInformation(
                "Retrying Gemini JSON repair: {url}. Model: {model}, source news count: {count}, prompt length: {promptLength}",
                apiUrl.Split('?')[0] + "?key=HIDDEN",
                model,
                sourceItemCount,
                prompt.Length);

            var stopwatch = Stopwatch.StartNew();
            var response = await _httpClient.PostAsync(apiUrl, requestContent, cancellationToken);
            stopwatch.Stop();
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Gemini JSON repair API Error after {elapsedMs} ms: {status} - {msg}",
                    stopwatch.ElapsedMilliseconds,
                    response.StatusCode,
                    responseContent);
                return null;
            }

            _logger.LogInformation("Gemini JSON repair call succeeded in {elapsedMs} ms", stopwatch.ElapsedMilliseconds);

            try
            {
                using var doc = JsonDocument.Parse(responseContent);
                return doc.RootElement
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text")
                    .GetString();
            }
            catch (JsonException ex)
            {
                var failedPath = await SaveFailedGeminiResponseAsync(responseContent, "gemini-repair-wrapper-json", cancellationToken);
                _logger.LogError(ex, "Failed to parse Gemini JSON repair wrapper response. Raw response saved to: {path}", failedPath);
                return null;
            }
        }

        private static async Task<string> SaveFailedGeminiResponseAsync(string content, string prefix, CancellationToken cancellationToken)
        {
            var dataDir = ResolveDataDirectory();
            var failureDir = Path.Combine(dataDir, "gemini-failures");
            Directory.CreateDirectory(failureDir);

            var fileName = $"{prefix}-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.json";
            var path = Path.Combine(failureDir, fileName);
            await File.WriteAllTextAsync(path, content, Encoding.UTF8, cancellationToken);
            return path;
        }

        private static IEnumerable<Albatross.Shared.Models.NewsItem> NormalizeRelatedArticles(
            IEnumerable<Albatross.Shared.Models.NewsItem> items,
            IReadOnlyDictionary<string, string> titleByUrl)
        {
            foreach (var item in items)
            {
                if (item.RelatedArticles.Count == 0 && item.RelatedUrls.Count > 0)
                {
                    item.RelatedArticles = item.RelatedUrls
                        .Where(url => !string.IsNullOrWhiteSpace(url))
                        .Select(url => new Albatross.Shared.Models.RelatedArticle
                        {
                            Title = titleByUrl.TryGetValue(url, out var title) ? title : url,
                            Url = url
                        })
                        .ToList();
                }

                if (item.RelatedUrls.Count == 0 && item.RelatedArticles.Count > 0)
                {
                    item.RelatedUrls = item.RelatedArticles
                        .Where(article => !string.IsNullOrWhiteSpace(article.Url))
                        .Select(article => article.Url)
                        .ToList();
                }

                foreach (var article in item.RelatedArticles.Where(article => !string.IsNullOrWhiteSpace(article.Url)))
                {
                    article.Title = titleByUrl.TryGetValue(article.Url, out var title) ? title : article.Url;
                }

                yield return item;
            }
        }

        private static readonly string[] KboKeywords =
        [
            "KBO", "프로야구", "LG", "삼성", "KT", "KIA", "두산", "한화", "NC", "롯데", "SSG", "키움"
        ];

        /// <summary>
        /// 오늘 날짜의 KBO 관련 기사를 모아 Gemma로 하이라이트를 재작성하고 KboDateHighlights에 저장한다.
        /// 뉴스 원문을 그대로 노출하면 저작권 문제가 있으므로, 화면에는 이 재작성된 텍스트만 사용한다.
        /// </summary>
        private async Task RunKboDateHighlightAsync(string databasePath, CancellationToken ct)
        {
            var today = GetKoreaNow().ToString("yyyy-MM-dd");

            try
            {
                await using var connection = new SqliteConnection($"Data Source={databasePath}");
                await connection.OpenAsync(ct);

                var likeClauses = string.Join(" OR ", KboKeywords.Select((_, i) => $"Title LIKE $kw{i} OR Content LIKE $kw{i}"));
                var cmd = connection.CreateCommand();
                cmd.CommandText =
                    $"""
                    SELECT Title, Content FROM RawNews
                    WHERE Content IS NOT NULL AND date(PublishedAt) = date($today)
                      AND ({likeClauses})
                    ORDER BY PublishedAt DESC
                    LIMIT 8;
                    """;
                cmd.Parameters.AddWithValue("$today", today);
                for (int i = 0; i < KboKeywords.Length; i++)
                {
                    cmd.Parameters.AddWithValue($"$kw{i}", $"%{KboKeywords[i]}%");
                }

                var articles = new List<(string Title, string Content)>();
                await using (var reader = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        var content = reader.GetString(1);
                        if (content.Length > 400)
                        {
                            content = content[..400];
                        }
                        articles.Add((reader.GetString(0), content));
                    }
                }

                if (articles.Count == 0)
                {
                    _logger.LogInformation("오늘({date}) KBO 관련 기사를 찾지 못해 하이라이트를 갱신하지 않습니다.", today);
                    return;
                }

                var highlight = await _classifier.SummarizeDateHighlightAsync(today, articles, ct);
                if (string.IsNullOrWhiteSpace(highlight))
                {
                    _logger.LogWarning("KBO 하이라이트 생성 실패 ({date}) — Gemma 응답이 비어있음", today);
                    return;
                }

                var upsertCmd = connection.CreateCommand();
                upsertCmd.CommandText =
                    """
                    INSERT INTO KboDateHighlights (GameDate, HighlightText, SourceCount, UpdatedAt)
                    VALUES ($gameDate, $highlight, $sourceCount, CURRENT_TIMESTAMP)
                    ON CONFLICT(GameDate) DO UPDATE SET
                        HighlightText = excluded.HighlightText,
                        SourceCount = excluded.SourceCount,
                        UpdatedAt = CURRENT_TIMESTAMP;
                    """;
                upsertCmd.Parameters.AddWithValue("$gameDate", today);
                upsertCmd.Parameters.AddWithValue("$highlight", highlight);
                upsertCmd.Parameters.AddWithValue("$sourceCount", articles.Count);
                await upsertCmd.ExecuteNonQueryAsync(ct);

                _logger.LogInformation("[KBO 하이라이트] {date} 갱신 완료 (참고 기사 {count}건)\n  {highlight}", today, articles.Count, highlight);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "KBO 날짜별 하이라이트 생성 중 오류 발생");
            }
        }

        /// <summary>
        /// Blazor WASM(Albatross.Web)은 정적 파일만 fetch할 수 있으므로, KBO 데이터를 DB 파일과 같은
        /// 디렉터리(Albatross.Web/wwwroot/data)에 JSON으로 내보낸다.
        /// </summary>
        private static async Task ExportKboDataToJsonAsync(string databasePath, CancellationToken ct)
        {
            var dataDir = Path.GetDirectoryName(databasePath);
            if (string.IsNullOrWhiteSpace(dataDir)) return;

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(ct);

            await ExportStandingsAsync(connection, dataDir, ct);
            await ExportPlayerStatsAsync(connection, dataDir, ct);
            await ExportGamesAsync(connection, dataDir, ct);
            await ExportPlayerTrendsAsync(connection, dataDir, ct);
            await ExportTeamTrendsAsync(connection, dataDir, ct);
        }

        /// <summary>
        /// --boxscore-only 가벼운 모드용 — 박스스코어 수집만으로 값이 바뀔 수 있는 kbo-games.json만 다시 내보낸다.
        /// </summary>
        private static async Task ExportGamesOnlyAsync(string databasePath, CancellationToken ct)
        {
            var dataDir = Path.GetDirectoryName(databasePath);
            if (string.IsNullOrWhiteSpace(dataDir)) return;

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(ct);

            await ExportGamesAsync(connection, dataDir, ct);
        }

        /// <summary>
        /// 시즌 시작(소급 백필된 가장 이른 날짜)부터 오늘까지 팀별 지표 추이를 전부 내보낸다. 프론트엔드가
        /// 기본으로는 최근 15일만 보여주고, 사용자가 직접 날짜 범위를 선택하면 이 전체 데이터에서 잘라 쓴다
        /// (그때그때 다시 export를 돌릴 필요 없이 한 번에 로드해서 클라이언트에서 필터링).
        /// </summary>
        private static async Task ExportTeamTrendsAsync(SqliteConnection connection, string dataDir, CancellationToken ct)
        {
            var dates = await CollectGameDatesAsync(connection, "KboTeamStandings", ct);

            var result = new Albatross.Shared.Models.KboTeamTrendsDto
            {
                Dates = dates,
                WinRates = await BuildTeamTrendAsync(connection, "WinRate", dates, sortDescending: true, ct),
                Avgs = await BuildTeamTrendAsync(connection, "Avg", dates, sortDescending: true, ct),
                Obps = await BuildTeamTrendAsync(connection, "Obp", dates, sortDescending: true, ct),
                Eras = await BuildTeamTrendAsync(connection, "Era", dates, sortDescending: false, ct),
                Oavgs = await BuildTeamTrendAsync(connection, "Oavg", dates, sortDescending: false, ct)
            };

            await File.WriteAllTextAsync(Path.Combine(dataDir, "kbo-team-trends.json"), JsonSerializer.Serialize(result, JsonOptions), ct);
        }

        private static async Task<List<Albatross.Shared.Models.KboTeamTrendDto>> BuildTeamTrendAsync(
            SqliteConnection connection, string valueColumn, List<string> dates, bool sortDescending, CancellationToken ct)
        {
            var direction = sortDescending ? "DESC" : "ASC";
            var topCmd = connection.CreateCommand();
            topCmd.CommandText =
                $"""
                SELECT TeamName
                FROM KboTeamStandings
                WHERE CollectedAt = (SELECT MAX(CollectedAt) FROM KboTeamStandings)
                  AND {valueColumn} IS NOT NULL
                ORDER BY {valueColumn} {direction};
                """;

            var teams = new List<string>();
            await using (var reader = await topCmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    teams.Add(reader.GetString(0));
                }
            }

            var trends = new List<Albatross.Shared.Models.KboTeamTrendDto>();
            foreach (var team in teams)
            {
                var byDate = new Dictionary<string, double?>();

                var pointCmd = connection.CreateCommand();
                pointCmd.CommandText =
                    $"""
                    SELECT date(CollectedAt, '+9 hours') AS d, {valueColumn}, MAX(CollectedAt)
                    FROM KboTeamStandings
                    WHERE TeamName = $name
                    GROUP BY d;
                    """;
                pointCmd.Parameters.AddWithValue("$name", team);

                await using (var reader = await pointCmd.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        byDate[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetDouble(1);
                    }
                }

                trends.Add(new Albatross.Shared.Models.KboTeamTrendDto
                {
                    TeamName = team,
                    Values = dates.Select(d => byDate.TryGetValue(d, out var v) ? v : null).ToList()
                });
            }

            return trends;
        }

        /// <summary>
        /// 시즌 시작(소급 백필된 가장 이른 스냅샷)부터 오늘까지 상위 15명(홈런/탈삼진 등)의 날짜별 누적 기록
        /// 추이를 전부 내보낸다. 팀 트렌드와 마찬가지로 프론트엔드가 기본으로 최근 15일만 보여주고,
        /// 사용자가 날짜 범위를 선택하면 이 전체 데이터에서 클라이언트 측에서 잘라 쓴다.
        /// </summary>
        private static async Task ExportPlayerTrendsAsync(SqliteConnection connection, string dataDir, CancellationToken ct)
        {
            var dates = await CollectGameDatesAsync(connection, "KboBatterStats", ct);

            var result = new Albatross.Shared.Models.KboPlayerTrendsDto
            {
                Dates = dates,
                BatterHomeRuns = await BuildPlayerTrendAsync(connection, "KboBatterStats", "HomeRuns", dates, sortDescending: true, ct, fromAllPlayers: true),
                BatterAvg = await BuildPlayerTrendAsync(connection, "KboBatterStats", "Avg", dates, sortDescending: true, ct),
                BatterHits = await BuildPlayerTrendAsync(connection, "KboBatterStats", "Hits", dates, sortDescending: true, ct, fromAllPlayers: true),
                BatterObp = await BuildPlayerTrendAsync(connection, "KboBatterStats", "Obp", dates, sortDescending: true, ct),
                BatterRbi = await BuildPlayerTrendAsync(connection, "KboBatterStats", "Rbi", dates, sortDescending: true, ct, fromAllPlayers: true),
                BatterGames = await BuildPlayerTrendAsync(connection, "KboBatterStats", "Games", dates, sortDescending: true, ct, fromAllPlayers: true),
                BatterAtBats = await BuildPlayerTrendAsync(connection, "KboBatterStats", "AtBats", dates, sortDescending: true, ct, fromAllPlayers: true),
                BatterRuns = await BuildPlayerTrendAsync(connection, "KboBatterStats", "Runs", dates, sortDescending: true, ct, fromAllPlayers: true),
                BatterDoubles = await BuildPlayerTrendAsync(connection, "KboBatterStats", "Doubles", dates, sortDescending: true, ct, fromAllPlayers: true),
                BatterTriples = await BuildPlayerTrendAsync(connection, "KboBatterStats", "Triples", dates, sortDescending: true, ct, fromAllPlayers: true),
                BatterStolenBases = await BuildPlayerTrendAsync(connection, "KboBatterStats", "StolenBases", dates, sortDescending: true, ct, fromAllPlayers: true),
                BatterWalks = await BuildPlayerTrendAsync(connection, "KboBatterStats", "Walks", dates, sortDescending: true, ct, fromAllPlayers: true),
                BatterHbp = await BuildPlayerTrendAsync(connection, "KboBatterStats", "Hbp", dates, sortDescending: true, ct, fromAllPlayers: true),
                BatterStrikeouts = await BuildPlayerTrendAsync(connection, "KboBatterStats", "Strikeouts", dates, sortDescending: true, ct, fromAllPlayers: true),
                PitcherStrikeouts = await BuildPlayerTrendAsync(connection, "KboPitcherStats", "Strikeouts", dates, sortDescending: true, ct, fromAllPlayers: true),
                PitcherEra = await BuildPlayerTrendAsync(connection, "KboPitcherStats", "Era", dates, sortDescending: false, ct),
                PitcherOavg = await BuildPlayerTrendAsync(connection, "KboPitcherStats", "Oavg", dates, sortDescending: false, ct),
                PitcherGames = await BuildPlayerTrendAsync(connection, "KboPitcherStats", "Games", dates, sortDescending: true, ct, fromAllPlayers: true),
                PitcherWins = await BuildPlayerTrendAsync(connection, "KboPitcherStats", "Wins", dates, sortDescending: true, ct, fromAllPlayers: true),
                PitcherLosses = await BuildPlayerTrendAsync(connection, "KboPitcherStats", "Losses", dates, sortDescending: true, ct, fromAllPlayers: true),
                PitcherSaves = await BuildPlayerTrendAsync(connection, "KboPitcherStats", "Saves", dates, sortDescending: true, ct, fromAllPlayers: true),
                PitcherHolds = await BuildPlayerTrendAsync(connection, "KboPitcherStats", "Holds", dates, sortDescending: true, ct, fromAllPlayers: true),
                PitcherInnings = await BuildPlayerTrendAsync(connection, "KboPitcherStats", "InningsDecimal", dates, sortDescending: true, ct, fromAllPlayers: true),
                PitcherHitsAllowed = await BuildPlayerTrendAsync(connection, "KboPitcherStats", "HitsAllowed", dates, sortDescending: true, ct, fromAllPlayers: true),
                PitcherHomeRunsAllowed = await BuildPlayerTrendAsync(connection, "KboPitcherStats", "HomeRuns", dates, sortDescending: true, ct, fromAllPlayers: true),
                PitcherRunsAllowed = await BuildPlayerTrendAsync(connection, "KboPitcherStats", "RunsAllowed", dates, sortDescending: true, ct, fromAllPlayers: true),
                PitcherEarnedRuns = await BuildPlayerTrendAsync(connection, "KboPitcherStats", "EarnedRuns", dates, sortDescending: true, ct, fromAllPlayers: true),
                PitcherWalks = await BuildPlayerTrendAsync(connection, "KboPitcherStats", "Walks", dates, sortDescending: true, ct, fromAllPlayers: true),
                PitcherHbp = await BuildPlayerTrendAsync(connection, "KboPitcherStats", "Hbp", dates, sortDescending: true, ct, fromAllPlayers: true),
                PitcherWinRate = await BuildPlayerTrendAsync(connection, "KboPitcherStats", "WinRate", dates, sortDescending: true, ct)
            };

            await File.WriteAllTextAsync(Path.Combine(dataDir, "kbo-player-trends.json"), JsonSerializer.Serialize(result, JsonOptions), ct);
        }

        /// <summary>
        /// 추이 차트의 x축 날짜 목록 — 달력 전체가 아니라 "실제 경기가 끝난 날짜"만 포함한다.
        /// 월요일/우천취소/올스타 브레이크처럼 경기가 없는 날은 수집기가 그날 돌았더라도(라이브 스냅샷이 있어도)
        /// 값이 전날과 같은 평평한 점만 생기므로 차트에서 제외하는 것이 맞다.
        /// snapshotTable의 가장 이른 스냅샷 날짜 이전(데이터가 아예 없는 구간)도 잘라낸다.
        /// </summary>
        private static async Task<List<string>> CollectGameDatesAsync(SqliteConnection connection, string snapshotTable, CancellationToken ct)
        {
            var datesCmd = connection.CreateCommand();
            datesCmd.CommandText =
                $"""
                SELECT DISTINCT GameDate FROM KboGameResults
                WHERE AwayScore IS NOT NULL AND HomeScore IS NOT NULL
                  AND GameDate >= (SELECT MIN(date(CollectedAt, '+9 hours')) FROM {snapshotTable})
                  AND GameDate <= date('now', '+9 hours')
                ORDER BY GameDate;
                """;

            var dates = new List<string>();
            await using var reader = await datesCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                dates.Add(reader.GetString(0));
            }

            return dates;
        }

        private static async Task<List<Albatross.Shared.Models.KboMetricTrendDto>> BuildPlayerTrendAsync(
            SqliteConnection connection, string table, string valueColumn, List<string> dates, bool sortDescending, CancellationToken ct,
            bool fromAllPlayers = false)
        {
            var direction = sortDescending ? "DESC" : "ASC";
            var topCmd = connection.CreateCommand();
            // 상위 15명 선정 기준이 지표 성격에 따라 다르다:
            //  - 누적 지표(홈런/안타/타점/탈삼진, fromAllPlayers=true): 전체 선수의 최신 스냅샷 값에서 선정.
            //    라이브 스냅샷은 공식 사이트 리더보드(타율/방어율 순 상위 30명)만 담고 있어서, 거기서만 뽑으면
            //    타율 순위권 밖의 진짜 홈런/타점 상위자가 누락된다.
            //  - 비율 지표(타율/출루율/방어율/피안타율): 최신 라이브 스냅샷에서 선정. 공식 리더보드 등재 자체가
            //    규정타석/규정이닝 충족을 의미하므로, 전체 선수 대상으로 하면 2타수 1안타 같은 표본이 상위를 차지한다.
            topCmd.CommandText = fromAllPlayers
                ? $"""
                   SELECT PlayerName, Team, {valueColumn}, MAX(CollectedAt)
                   FROM {table}
                   WHERE {valueColumn} IS NOT NULL
                   GROUP BY PlayerName, Team
                   ORDER BY {valueColumn} {direction}
                   LIMIT 15;
                   """
                : $"""
                   SELECT PlayerName, Team
                   FROM {table}
                   WHERE CollectedAt = (SELECT MAX(CollectedAt) FROM {table})
                     AND {valueColumn} IS NOT NULL
                   ORDER BY {valueColumn} {direction}
                   LIMIT 15;
                   """;

            var top = new List<(string Name, string? Team)>();
            await using (var reader = await topCmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    top.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
                }
            }

            var trends = new List<Albatross.Shared.Models.KboMetricTrendDto>();
            foreach (var (name, team) in top)
            {
                var byDate = new Dictionary<string, double?>();

                var pointCmd = connection.CreateCommand();
                // SQLite의 bare-column 관용 규칙: MAX(CollectedAt)를 함께 SELECT하면
                // valueColumn은 그 최댓값을 만든 행(=그 날 마지막 수집)의 값을 그대로 반환한다.
                pointCmd.CommandText =
                    $"""
                    SELECT date(CollectedAt, '+9 hours') AS d, {valueColumn}, MAX(CollectedAt)
                    FROM {table}
                    WHERE PlayerName = $name AND (Team = $team OR (Team IS NULL AND $team IS NULL))
                    GROUP BY d;
                    """;
                pointCmd.Parameters.AddWithValue("$name", name);
                pointCmd.Parameters.AddWithValue("$team", (object?)team ?? DBNull.Value);

                await using (var reader = await pointCmd.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        byDate[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetDouble(1);
                    }
                }

                trends.Add(new Albatross.Shared.Models.KboMetricTrendDto
                {
                    PlayerName = name,
                    Team = team,
                    Values = dates.Select(d => byDate.TryGetValue(d, out var v) ? v : null).ToList()
                });
            }

            return trends;
        }

        private static async Task ExportStandingsAsync(SqliteConnection connection, string dataDir, CancellationToken ct)
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT TeamName, Rank, Games, Wins, Losses, Draws, WinRate, GamesBehind, Avg, Obp, Era, Oavg
                FROM KboTeamStandings
                WHERE CollectedAt = (SELECT MAX(CollectedAt) FROM KboTeamStandings)
                ORDER BY Rank;
                """;

            var list = new List<Albatross.Shared.Models.KboStandingDto>();
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    list.Add(new Albatross.Shared.Models.KboStandingDto
                    {
                        TeamName = reader.GetString(0),
                        Rank = reader.GetInt32(1),
                        Games = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                        Wins = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                        Losses = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                        Draws = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                        WinRate = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                        GamesBehind = reader.IsDBNull(7) ? null : reader.GetString(7),
                        Avg = reader.IsDBNull(8) ? null : reader.GetDouble(8),
                        Obp = reader.IsDBNull(9) ? null : reader.GetDouble(9),
                        Era = reader.IsDBNull(10) ? null : reader.GetDouble(10),
                        Oavg = reader.IsDBNull(11) ? null : reader.GetDouble(11)
                    });
                }
            }

            await File.WriteAllTextAsync(Path.Combine(dataDir, "kbo-standings.json"), JsonSerializer.Serialize(list, JsonOptions), ct);
        }

        private static async Task ExportPlayerStatsAsync(SqliteConnection connection, string dataDir, CancellationToken ct)
        {
            var result = new Albatross.Shared.Models.KboPlayerStatsDto();

            var batterCmd = connection.CreateCommand();
            // 도루(StolenBases)는 공식 선수기록 페이지에 없어 라이브 스냅샷에선 항상 NULL이다 —
            // 선수별로 가장 최근의 non-null 값(박스스코어 소급분, 보통 어제 기준)을 서브쿼리로 보충한다.
            batterCmd.CommandText =
                """
                SELECT b.PlayerName, b.Team, b.Avg, b.Games, b.Hits, b.HomeRuns, b.Rbi, b.Obp,
                       b.AtBats, b.Runs, b.Doubles, b.Triples, b.Walks, b.Hbp, b.Strikeouts,
                       COALESCE(b.StolenBases,
                                (SELECT s.StolenBases FROM KboBatterStats s
                                 WHERE s.PlayerName = b.PlayerName AND s.Team = b.Team AND s.StolenBases IS NOT NULL
                                 ORDER BY s.CollectedAt DESC LIMIT 1)) AS StolenBases
                FROM KboBatterStats b
                WHERE b.CollectedAt = (SELECT MAX(CollectedAt) FROM KboBatterStats);
                """;
            await using (var reader = await batterCmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    result.Batters.Add(new Albatross.Shared.Models.KboBatterStatDto
                    {
                        PlayerName = reader.GetString(0),
                        Team = reader.IsDBNull(1) ? null : reader.GetString(1),
                        Avg = reader.IsDBNull(2) ? null : reader.GetDouble(2),
                        Games = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                        Hits = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                        HomeRuns = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                        Rbi = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                        Obp = reader.IsDBNull(7) ? null : reader.GetDouble(7),
                        AtBats = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                        Runs = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                        Doubles = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                        Triples = reader.IsDBNull(11) ? null : reader.GetInt32(11),
                        Walks = reader.IsDBNull(12) ? null : reader.GetInt32(12),
                        Hbp = reader.IsDBNull(13) ? null : reader.GetInt32(13),
                        Strikeouts = reader.IsDBNull(14) ? null : reader.GetInt32(14),
                        StolenBases = reader.IsDBNull(15) ? null : reader.GetInt32(15)
                    });
                }
            }

            var pitcherCmd = connection.CreateCommand();
            pitcherCmd.CommandText =
                """
                SELECT PlayerName, Team, Era, Wins, Losses, Saves, Innings, Strikeouts, Oavg,
                       Games, Holds, HitsAllowed, HomeRuns, RunsAllowed, EarnedRuns, Walks, Hbp, WinRate, InningsDecimal
                FROM KboPitcherStats
                WHERE CollectedAt = (SELECT MAX(CollectedAt) FROM KboPitcherStats);
                """;
            await using (var reader = await pitcherCmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    result.Pitchers.Add(new Albatross.Shared.Models.KboPitcherStatDto
                    {
                        PlayerName = reader.GetString(0),
                        Team = reader.IsDBNull(1) ? null : reader.GetString(1),
                        Era = reader.IsDBNull(2) ? null : reader.GetDouble(2),
                        Wins = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                        Losses = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                        Saves = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                        Innings = reader.IsDBNull(6) ? null : reader.GetString(6),
                        Strikeouts = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                        Oavg = reader.IsDBNull(8) ? null : reader.GetDouble(8),
                        Games = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                        Holds = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                        HitsAllowed = reader.IsDBNull(11) ? null : reader.GetInt32(11),
                        HomeRunsAllowed = reader.IsDBNull(12) ? null : reader.GetInt32(12),
                        RunsAllowed = reader.IsDBNull(13) ? null : reader.GetInt32(13),
                        EarnedRuns = reader.IsDBNull(14) ? null : reader.GetInt32(14),
                        Walks = reader.IsDBNull(15) ? null : reader.GetInt32(15),
                        Hbp = reader.IsDBNull(16) ? null : reader.GetInt32(16),
                        WinRate = reader.IsDBNull(17) ? null : reader.GetDouble(17),
                        InningsDecimal = reader.IsDBNull(18) ? null : reader.GetDouble(18)
                    });
                }
            }

            await File.WriteAllTextAsync(Path.Combine(dataDir, "kbo-players.json"), JsonSerializer.Serialize(result, JsonOptions), ct);
        }

        private static async Task ExportGamesAsync(SqliteConnection connection, string dataDir, CancellationToken ct)
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT g.GameDate, g.GameTime, g.AwayTeam, g.AwayScore, g.HomeTeam, g.HomeScore, h.HighlightText
                FROM KboGameResults g
                LEFT JOIN KboDateHighlights h ON h.GameDate = g.GameDate
                ORDER BY g.GameDate DESC, g.GameTime;
                """;

            var byDate = new Dictionary<string, Albatross.Shared.Models.KboGameDayDto>();
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    var gameDate = reader.GetString(0);
                    if (!byDate.TryGetValue(gameDate, out var day))
                    {
                        day = new Albatross.Shared.Models.KboGameDayDto
                        {
                            GameDate = gameDate,
                            Highlight = reader.IsDBNull(6) ? null : reader.GetString(6)
                        };
                        byDate[gameDate] = day;
                    }

                    day.Games.Add(new Albatross.Shared.Models.KboGameDto
                    {
                        GameTime = reader.IsDBNull(1) ? null : reader.GetString(1),
                        AwayTeam = reader.GetString(2),
                        AwayScore = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                        HomeTeam = reader.GetString(4),
                        HomeScore = reader.IsDBNull(5) ? null : reader.GetInt32(5)
                    });
                }
            }

            var list = byDate.Values.OrderByDescending(d => d.GameDate).ToList();
            foreach (var day in list)
            {
                day.Issues = await BuildPlayerIssuesAsync(connection, day.GameDate, ct);
            }

            await File.WriteAllTextAsync(Path.Combine(dataDir, "kbo-games.json"), JsonSerializer.Serialize(list, JsonOptions), ct);
        }

        /// <summary>
        /// 그 날짜에 실제로 출전한 타자들 중, 박스스코어 기반으로 최근 5경기 안타/홈런 실적이
        /// 눈에 띄는 선수를 데이터 기반 "핫이슈" 헤드라인으로 뽑아낸다.
        /// </summary>
        private static async Task<List<string>> BuildPlayerIssuesAsync(SqliteConnection connection, string gameDate, CancellationToken ct)
        {
            var playersCmd = connection.CreateCommand();
            playersCmd.CommandText = "SELECT DISTINCT PlayerName, Team FROM KboBoxScoreBatting WHERE GameDate = $gameDate;";
            playersCmd.Parameters.AddWithValue("$gameDate", gameDate);

            var players = new List<(string Name, string Team)>();
            await using (var reader = await playersCmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    players.Add((reader.GetString(0), reader.GetString(1)));
                }
            }

            var issues = new List<(int Priority, string Text)>();

            foreach (var (name, team) in players)
            {
                var recentCmd = connection.CreateCommand();
                recentCmd.CommandText =
                    """
                    SELECT Hits, HomeRuns FROM KboBoxScoreBatting
                    WHERE PlayerName = $name AND Team = $team AND GameDate <= $gameDate
                    ORDER BY GameDate DESC
                    LIMIT 5;
                    """;
                recentCmd.Parameters.AddWithValue("$name", name);
                recentCmd.Parameters.AddWithValue("$team", team);
                recentCmd.Parameters.AddWithValue("$gameDate", gameDate);

                var games = new List<(int Hits, int HomeRuns)>();
                await using (var reader = await recentCmd.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        games.Add((
                            reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                            reader.IsDBNull(1) ? 0 : reader.GetInt32(1)));
                    }
                }

                // 최근 5경기 데이터가 다 쌓이지 않았으면 판단을 보류한다 (박스스코어 이력이 아직 부족한 시즌 초반 등)
                if (games.Count < 5) continue;

                var totalHits = games.Sum(g => g.Hits);
                var totalHomeRuns = games.Sum(g => g.HomeRuns);

                var hitStreak = 0;
                foreach (var g in games)
                {
                    if (g.Hits > 0) hitStreak++;
                    else break;
                }

                if (hitStreak >= 5)
                {
                    issues.Add((hitStreak, $"{name}({team}) {hitStreak}경기 연속 안타"));
                }
                else if (totalHits >= 7)
                {
                    issues.Add((totalHits, $"{name}({team}) 최근 5경기 안타 {totalHits}개"));
                }

                if (totalHomeRuns >= 2)
                {
                    issues.Add((totalHomeRuns + 10, $"{name}({team}) 최근 5경기 홈런 {totalHomeRuns}개"));
                }
            }

            return issues
                .OrderByDescending(i => i.Priority)
                .Select(i => i.Text)
                .Take(8)
                .ToList();
        }

        private async Task RunCategoryClassificationAsync(string databasePath, CancellationToken ct)
        {
            _logger.LogInformation("Starting category classification for articles with Content and ImageUrl...");

            try
            {
                await using var connection = new SqliteConnection($"Data Source={databasePath}");
                await connection.OpenAsync(ct);

                // 1. 분류 대상 기사 조회 (Content와 ImageUrl이 있고 아직 매핑되지 않은 기사 - 최근 수집된 기사 최우선)
                var selectCmd = connection.CreateCommand();
                selectCmd.CommandText = 
                    """
                    SELECT Id, Title, Content 
                    FROM RawNews 
                    WHERE Content IS NOT NULL 
                      AND ImageUrl IS NOT NULL 
                      AND Id NOT IN (SELECT NewsId FROM NewsCategoryMapping)
                    ORDER BY PublishedAt DESC;
                    """;

                var targetArticles = new List<(string Id, string Title, string Content)>();
                await using (var reader = await selectCmd.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        targetArticles.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
                    }
                }

                if (targetArticles.Count == 0)
                {
                    _logger.LogInformation("No new articles to classify.");
                    return;
                }

                // 2. 가용 카테고리 조회
                var catCmd = connection.CreateCommand();
                catCmd.CommandText = "SELECT CategoryCode, CategoryName FROM Categories;";
                var availableCategories = new List<(string Code, string Name)>();
                await using (var reader = await catCmd.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        availableCategories.Add((reader.GetString(0), reader.GetString(1)));
                    }
                }

                // 🚀 [제한 추가] 상위 10개 기사만 먼저 분석 진행
                var limitedTargets = targetArticles.Take(10).ToList();
                _logger.LogInformation("Classifying {count} articles (limited from {total}) using Gemma AI...", limitedTargets.Count, targetArticles.Count);

                foreach (var article in limitedTargets)
                {
                    var analysisResult = await _classifier.Classify3LevelsAsync(article.Id, article.Title, article.Content, databasePath, ct);
                    
                    if (analysisResult.Categories.Count > 0)
                    {
                        await using var transaction = await connection.BeginTransactionAsync(ct);
                        var sqliteTrans = (SqliteTransaction)transaction;
                        try
                        {
                            // AI 응답의 카테고리명을 Code/Name 형식으로 변환하여 저장
                            var structuredCategories = new List<Dictionary<string, string>>();
                            foreach (var cat in analysisResult.Categories)
                            {
                                var l1Code = await GetOrCreateCategoryAsync(connection, sqliteTrans, cat.Level1, 1, null, "FL", ct);
                                var l2Code = await GetOrCreateCategoryAsync(connection, sqliteTrans, cat.Level2, 2, l1Code, "S2L", ct);
                                var l3Code = await GetOrCreateCategoryAsync(connection, sqliteTrans, cat.Level3, 3, l2Code, "S3L", ct);
                                
                                // 매핑 테이블 업데이트
                                if (!string.IsNullOrEmpty(l3Code))
                                {
                                    var insertMappingCmd = connection.CreateCommand();
                                    insertMappingCmd.Transaction = sqliteTrans;
                                    insertMappingCmd.CommandText = "INSERT OR IGNORE INTO NewsCategoryMapping (NewsId, CategoryCode) VALUES ($newsId, $code);";
                                    insertMappingCmd.Parameters.AddWithValue("$newsId", article.Id);
                                    insertMappingCmd.Parameters.AddWithValue("$code", l3Code);
                                    await insertMappingCmd.ExecuteNonQueryAsync(ct);
                                }

                                // 저장용 구조체 생성
                                structuredCategories.Add(new Dictionary<string, string>
                                {
                                    { "Level1", $"{l1Code}/{cat.Level1}" },
                                    { "Level2", $"{l2Code}/{cat.Level2}" },
                                    { "Level3", $"{l3Code}/{cat.Level3}" },
                                    { "Summary", cat.Summary }
                                });

                                // NewsSummaryMapping 테이블에 데이터 삽입
                                var insertSummaryMappingCmd = connection.CreateCommand();
                                insertSummaryMappingCmd.Transaction = sqliteTrans;
                                insertSummaryMappingCmd.CommandText = 
                                    """
                                    INSERT INTO NewsSummaryMapping (NewsId, Level_1, Level_2, Level_3, Summary)
                                    VALUES ($newsId, $l1, $l2, $l3, $summary);
                                    """;
                                insertSummaryMappingCmd.Parameters.AddWithValue("$newsId", article.Id);
                                insertSummaryMappingCmd.Parameters.AddWithValue("$l1", $"{l1Code}/{cat.Level1}");
                                insertSummaryMappingCmd.Parameters.AddWithValue("$l2", $"{l2Code}/{cat.Level2}");
                                insertSummaryMappingCmd.Parameters.AddWithValue("$l3", $"{l3Code}/{cat.Level3}");
                                insertSummaryMappingCmd.Parameters.AddWithValue("$summary", cat.Summary);
                                await insertSummaryMappingCmd.ExecuteNonQueryAsync(ct);
                            }
                            
                            // 프롬프트 및 변환된 코드 응답 저장
                            var updatePromptCmd = connection.CreateCommand();
                            updatePromptCmd.Transaction = sqliteTrans;
                            updatePromptCmd.CommandText = "UPDATE RawNews SET ReqPrompt = $req, ResPrompt = $res WHERE Id = $id;";
                            updatePromptCmd.Parameters.AddWithValue("$req", analysisResult.ReqPrompt);
                            
                            var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                            updatePromptCmd.Parameters.AddWithValue("$res", JsonSerializer.Serialize(structuredCategories, jsonOptions));
                            updatePromptCmd.Parameters.AddWithValue("$id", article.Id);
                            await updatePromptCmd.ExecuteNonQueryAsync(ct);

                            await transaction.CommitAsync(ct);
                            _logger.LogInformation("Article '{title}' assigned to dynamic 3-level categories.", article.Title);
                        }
                        catch (Exception ex)
                        {
                            await transaction.RollbackAsync(ct);
                            _logger.LogError(ex, "Failed to save dynamic category mapping for article: {id}", article.Id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during RunCategoryClassificationAsync");
            }
        }

        public async Task ClassifySingleNewsAsync(string newsId, CancellationToken ct)
        {
            var databasePath = _config["Collector:DatabasePath"];
            if (string.IsNullOrWhiteSpace(databasePath)) databasePath = Path.Combine(ResolveDataDirectory(), "albatross-news.db");
            else databasePath = Path.GetFullPath(databasePath);

            _logger.LogInformation("Starting single-item classification for newsId: {newsId}", newsId);

            try
            {
                await using var connection = new SqliteConnection($"Data Source={databasePath}");
                await connection.OpenAsync(ct);

                var selectCmd = connection.CreateCommand();
                selectCmd.CommandText = "SELECT Id, Title, Content FROM RawNews WHERE Id = $id;";
                selectCmd.Parameters.AddWithValue("$id", newsId);

                string? id = null;
                string? title = null;
                string? content = null;

                await using (var reader = await selectCmd.ExecuteReaderAsync(ct))
                {
                    if (await reader.ReadAsync(ct))
                    {
                        id = reader.GetString(0);
                        title = reader.GetString(1);
                        content = reader.IsDBNull(2) ? null : reader.GetString(2);
                    }
                }

                if (id == null || string.IsNullOrEmpty(content))
                {
                    _logger.LogWarning("News article not found or content empty for ID: {id}", newsId);
                    return;
                }

                // 분석 및 저장
                var analysisResult = await _classifier.Classify3LevelsAsync(id, title, content, databasePath, ct);
                
                if (analysisResult.Categories.Count > 0)
                {
                    await using var transaction = await connection.BeginTransactionAsync(ct);
                    var sqliteTrans = (SqliteTransaction)transaction;

                    try
                    {
                        // 1. RawNews 업데이트 (프롬프트 저장)
                        var updatePromptCmd = connection.CreateCommand();
                        updatePromptCmd.Transaction = sqliteTrans;
                        updatePromptCmd.CommandText = "UPDATE RawNews SET ReqPrompt = $req, ResPrompt = $res, UpdatedAt = CURRENT_TIMESTAMP WHERE Id = $id;";
                        updatePromptCmd.Parameters.AddWithValue("$req", analysisResult.ReqPrompt);
                        var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                        updatePromptCmd.Parameters.AddWithValue("$res", JsonSerializer.Serialize(analysisResult.Categories, jsonOptions));
                        updatePromptCmd.Parameters.AddWithValue("$id", id);
                        await updatePromptCmd.ExecuteNonQueryAsync(ct);

                        // 2. 관련 테이블 삭제 (NewsCategoryMapping, NewsSummaryMapping)
                        var deleteMappingCmd = connection.CreateCommand();
                        deleteMappingCmd.Transaction = sqliteTrans;
                        deleteMappingCmd.CommandText = "DELETE FROM NewsCategoryMapping WHERE NewsId = $id;";
                        deleteMappingCmd.Parameters.AddWithValue("$id", id);
                        await deleteMappingCmd.ExecuteNonQueryAsync(ct);

                        var deleteSummaryCmd = connection.CreateCommand();
                        deleteSummaryCmd.Transaction = sqliteTrans;
                        deleteSummaryCmd.CommandText = "DELETE FROM NewsSummaryMapping WHERE NewsId = $id;";
                        deleteSummaryCmd.Parameters.AddWithValue("$id", id);
                        await deleteSummaryCmd.ExecuteNonQueryAsync(ct);

                        var structuredCategories = new List<Dictionary<string, string>>();

                        foreach (var cat in analysisResult.Categories)
                        {
                            // 3. Categories 확인 및 삽입
                            var l1Code = await GetOrCreateCategoryAsync(connection, sqliteTrans, cat.Level1, 1, null, "FL", ct);
                            var l2Code = await GetOrCreateCategoryAsync(connection, sqliteTrans, cat.Level2, 2, l1Code, "S2L", ct);
                            var l3Code = await GetOrCreateCategoryAsync(connection, sqliteTrans, cat.Level3, 3, l2Code, "S3L", ct);
                            
                            // 4. 매핑 테이블 재삽입
                            if (!string.IsNullOrEmpty(l3Code))
                            {
                                var insertMappingCmd = connection.CreateCommand();
                                insertMappingCmd.Transaction = sqliteTrans;
                                insertMappingCmd.CommandText = "INSERT INTO NewsCategoryMapping (NewsId, CategoryCode) VALUES ($newsId, $code);";
                                insertMappingCmd.Parameters.AddWithValue("$newsId", id);
                                insertMappingCmd.Parameters.AddWithValue("$code", l3Code);
                                await insertMappingCmd.ExecuteNonQueryAsync(ct);
                            }

                            // 5. NewsSummaryMapping 재삽입
                            var insertSummaryMappingCmd = connection.CreateCommand();
                            insertSummaryMappingCmd.Transaction = sqliteTrans;
                            insertSummaryMappingCmd.CommandText = 
                                """
                                INSERT INTO NewsSummaryMapping (NewsId, Level_1, Level_2, Level_3, Summary)
                                VALUES ($newsId, $l1, $l2, $l3, $summary);
                                """;
                            insertSummaryMappingCmd.Parameters.AddWithValue("$newsId", id);
                            insertSummaryMappingCmd.Parameters.AddWithValue("$l1", $"{l1Code}/{cat.Level1}");
                            insertSummaryMappingCmd.Parameters.AddWithValue("$l2", $"{l2Code}/{cat.Level2}");
                            insertSummaryMappingCmd.Parameters.AddWithValue("$l3", $"{l3Code}/{cat.Level3}");
                            insertSummaryMappingCmd.Parameters.AddWithValue("$summary", cat.Summary);
                            await insertSummaryMappingCmd.ExecuteNonQueryAsync(ct);
                        }

                        await transaction.CommitAsync(ct);
                        _logger.LogInformation("Successfully reclassified and updated data for newsId: {id}", id);
                    }
                    catch (Exception ex)
                    {
                        await transaction.RollbackAsync(ct);
                        _logger.LogError(ex, "Failed to update reclassified data for newsId: {id}", id);
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reclassify newsId: {newsId}", newsId);
            }
        }

        private async Task<string> GetOrCreateCategoryAsync(SqliteConnection connection, SqliteTransaction transaction, string name, int level, string? parentCode, string prefix, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;

            // 1. 이미 존재하는지 확인 (이름, 레벨, 상위코드 기준)
            var selectCmd = connection.CreateCommand();
            selectCmd.Transaction = transaction;
            selectCmd.CommandText = 
                """
                SELECT CategoryCode FROM Categories 
                WHERE CategoryName = $name AND Level = $level 
                  AND (UpperCategoryCode = $parent OR (UpperCategoryCode IS NULL AND $parent IS NULL));
                """;
            selectCmd.Parameters.AddWithValue("$name", name.Trim());
            selectCmd.Parameters.AddWithValue("$level", level);
            selectCmd.Parameters.AddWithValue("$parent", (object?)parentCode ?? DBNull.Value);

            var existingCode = await selectCmd.ExecuteScalarAsync(ct) as string;
            if (!string.IsNullOrEmpty(existingCode))
            {
                await EnsureCategoryFullPathAsync(connection, transaction, existingCode, name.Trim(), parentCode, ct);
                return existingCode;
            }

            // 2. 존재하지 않으면 새로 생성
            // 현재 접두사(FL_, S2L_ 등)의 마지막 번호 조회
            var countCmd = connection.CreateCommand();
            countCmd.Transaction = transaction;
            countCmd.CommandText = "SELECT COUNT(*) FROM Categories WHERE CategoryCode LIKE $pattern;";
            countCmd.Parameters.AddWithValue("$pattern", $"{prefix}_%");
            var count = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));
            
            var newCode = $"{prefix}_{(count + 1):D3}";
            var fullPath = await BuildCategoryFullPathAsync(connection, transaction, name.Trim(), parentCode, ct);

            var insertCmd = connection.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = 
                """
                INSERT INTO Categories (CategoryCode, CategoryName, Level, UpperCategoryCode, FullPath) 
                VALUES ($code, $name, $level, $parent, $fullPath);
                """;
            insertCmd.Parameters.AddWithValue("$code", newCode);
            insertCmd.Parameters.AddWithValue("$name", name.Trim());
            insertCmd.Parameters.AddWithValue("$level", level);
            insertCmd.Parameters.AddWithValue("$parent", (object?)parentCode ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("$fullPath", fullPath);
            
            await insertCmd.ExecuteNonQueryAsync(ct);
            return newCode;
        }

        private static async Task EnsureCategoryFullPathAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string categoryCode,
            string categoryName,
            string? parentCode,
            CancellationToken ct)
        {
            var fullPath = await BuildCategoryFullPathAsync(connection, transaction, categoryName, parentCode, ct);

            var updateCmd = connection.CreateCommand();
            updateCmd.Transaction = transaction;
            updateCmd.CommandText =
                """
                UPDATE Categories
                SET FullPath = $fullPath
                WHERE CategoryCode = $code
                  AND (FullPath IS NULL OR FullPath = '');
                """;
            updateCmd.Parameters.AddWithValue("$fullPath", fullPath);
            updateCmd.Parameters.AddWithValue("$code", categoryCode);
            await updateCmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task<string> BuildCategoryFullPathAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string categoryName,
            string? parentCode,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(parentCode))
            {
                return categoryName;
            }

            var parentCmd = connection.CreateCommand();
            parentCmd.Transaction = transaction;
            parentCmd.CommandText = "SELECT FullPath, CategoryName FROM Categories WHERE CategoryCode = $parentCode;";
            parentCmd.Parameters.AddWithValue("$parentCode", parentCode);

            await using var reader = await parentCmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var parentFullPath = !reader.IsDBNull(0) ? reader.GetString(0) : reader.GetString(1);
                return $"{parentFullPath} > {categoryName}";
            }

            return categoryName;
        }

        private static DateTime GetKoreaNow()
        {
            try
            {
                var kstZone = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, kstZone);
            }
            catch (TimeZoneNotFoundException)
            {
                var kstZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, kstZone);
            }
        }
    }
}
