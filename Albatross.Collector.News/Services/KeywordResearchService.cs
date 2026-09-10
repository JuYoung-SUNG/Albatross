using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Albatross.Collector.News.Services;

/// <summary>
/// 블로그에 쓸 키워드를 조사한다.
///
///   시드 키워드 몇 개 입력
///     → 검색광고 API가 연관 키워드를 수백~수천 개 돌려준다 (검색량 포함)
///     → 검색량 구간(대형/중형/롱테일)으로 나눠 구간마다 상위 N개만 남긴다
///     → 남은 것만 블로그 검색으로 실제 경쟁을 확인한다
///     → 기회 점수 계산 후 저장
///
/// 구간을 나누는 이유:
///   검색량 순으로만 줄 세우면 대형 키워드가 화면을 다 먹는다.
///   그런데 "삼성전자주가"(월 2,700만) 같은 건 네이버 증권·뉴스가 차지하고 있어
///   블로그가 비집을 자리가 없다. 실제로 쓸 만한 건 중형·롱테일 구간이라
///   구간을 갈라서 각각 상위를 뽑아야 후보가 고르게 나온다.
///
/// 저장을 3장으로 나눈 이유:
///   검색량은 계절을 타서 한 시점만 보면 판단을 그르친다.
///   측정값을 시계열로 쌓아야 "뜨는 중인지 지는 중인지"가 보인다.
/// </summary>
public class KeywordResearchService
{
    private readonly NaverAutocompleteService _autocomplete;
    private readonly NaverSearchAdService _searchAd;
    private readonly NaverBlogSearchService _blog;
    private readonly ILogger<KeywordResearchService> _logger;

    public KeywordResearchService(
        NaverAutocompleteService autocomplete,
        NaverSearchAdService searchAd,
        NaverBlogSearchService blog,
        ILogger<KeywordResearchService> logger)
    {
        _autocomplete = autocomplete;
        _searchAd = searchAd;
        _blog = blog;
        _logger = logger;
    }

    /// <param name="PerTier">구간마다 블로그 경쟁을 확인할 최대 개수</param>
    /// <param name="UseRelated">검색광고 API의 연관 키워드까지 후보로 받을지</param>
    public sealed record Options(
        int ExpandDepth = 1,
        int MaxKeywords = 40,
        int MinMonthlySearch = 0,
        int TopPosts = 10,
        int PerTier = 30,
        bool UseRelated = true);

    /// <summary>
    /// 검색량 구간. 구간마다 노려야 할 방식이 다르다.
    ///   A-대형   유입은 크지만 포털·언론이 차지해 상위 노출이 매우 어렵다
    ///   B-중형   현실적으로 승산이 있는 구간
    ///   C-롱테일 쉽지만 하나로는 유입이 적어 여러 개를 모아야 한다
    /// </summary>
    public static string TierOf(int monthly) => monthly switch
    {
        >= 100_000 => "A-대형",
        >= 1_000 => "B-중형",
        >= 100 => "C-롱테일",
        _ => "D-미미"
    };

    public async Task<int> ResearchAsync(string databasePath, string seed, Options opt, CancellationToken ct)
        => await ResearchManyAsync(databasePath, new List<string> { seed }, opt, ct);

    /// <summary>시드 여러 개를 한 번에 조사한다. 연관 키워드가 겹치므로 묶어서 부르는 편이 효율적이다.</summary>
    public async Task<int> ResearchManyAsync(
        string databasePath, List<string> seeds, Options opt, CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={databasePath}");
        await conn.OpenAsync(ct);
        await EnsureTablesAsync(conn, ct);

        var now = DateTimeOffset.Now.ToString("O");
        var seedLabel = string.Join(", ", seeds);
        _logger.LogInformation("[키워드조사] 시드 [{seeds}] 시작", seedLabel);

        // 1) 후보 넓히기 — 자동완성으로 한 겹, 검색광고 연관어로 크게 한 겹
        var candidates = new List<string>(seeds);
        foreach (var s in seeds)
            candidates.AddRange(await _autocomplete.ExpandAsync(s, opt.ExpandDepth, opt.MaxKeywords, ct));
        candidates = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // 2) 검색량 조회. UseRelated면 요청하지 않은 연관 키워드까지 받아 후보가 수백~수천으로 늘어난다
        var volumes = await _searchAd.GetVolumesAsync(candidates, opt.UseRelated, ct);
        if (volumes.Count == 0)
        {
            _logger.LogError("[키워드조사] 검색량을 하나도 얻지 못했습니다 — NAVER_AD_* 설정을 확인하세요");
            return 0;
        }
        _logger.LogInformation("[키워드조사] 검색량 확보 {n}개 (자동완성 후보 {c}개에서 확장)",
            volumes.Count, candidates.Count);

        // 3) 구간별로 나눠 상위 N개만 남긴다 — 블로그 조회 횟수를 통제하면서 후보를 고르게 뽑는다
        var picked = volumes.Values
            .Where(v => v.MonthlyTotal >= Math.Max(opt.MinMonthlySearch, 100))
            .GroupBy(v => TierOf(v.MonthlyTotal))
            .SelectMany(g => g.OrderByDescending(v => v.MonthlyTotal).Take(opt.PerTier))
            .OrderByDescending(v => v.MonthlyTotal)
            .ToList();

        foreach (var g in picked.GroupBy(v => TierOf(v.MonthlyTotal)).OrderBy(g => g.Key))
            _logger.LogInformation("[키워드조사]   {tier} {n}개", g.Key, g.Count());

        var seedId = await InsertSeedAsync(conn, seedLabel, now, ct);

        // 4) 남은 것만 블로그 경쟁 확인
        var saved = 0;
        foreach (var vol in picked)
        {
            ct.ThrowIfCancellationRequested();

            var search = await _blog.SearchAsync(vol.Keyword, opt.TopPosts, ct);
            if (search is null) continue;

            var tier = TierOf(vol.MonthlyTotal);
            var score = CalcScore(vol.MonthlyTotal, search.ExactTitleMatches, search.Analyzed);
            var grade = Grade(search, vol.MonthlyTotal);

            var keywordId = await UpsertKeywordAsync(conn, vol.Keyword, seedId, now, ct);
            await InsertMetricAsync(conn, keywordId, now, vol, search, tier, score, grade, ct);
            saved++;

            if (saved % 25 == 0)
                _logger.LogInformation("[키워드조사]   진행 {n}/{all}", saved, picked.Count);

            await Task.Delay(120, ct);   // 검색 API 초당 호출 제한 회피
        }

        await UpdateSeedCountAsync(conn, seedId, saved, ct);
        _logger.LogInformation("[키워드조사] 완료 — 키워드 {n}개 저장", saved);
        return saved;
    }

    /// <summary>
    /// 기회 점수 = 월간 검색수 ÷ (제목에 그 키워드를 정확히 쓴 글의 비율).
    /// 상위 글 중 정확히 겨냥한 글이 적을수록 비집고 들어갈 자리가 크다.
    /// </summary>
    private static double CalcScore(int monthlySearch, int exactTitle, int analyzed)
    {
        if (analyzed <= 0) return 0;
        var occupied = (exactTitle + 0.5) / analyzed;   // 0이어도 무한대가 되지 않도록 보정
        return Math.Round(monthlySearch / occupied / 1000.0, 3);
    }

    /// <summary>
    /// 등급. 검색량을 모르면(검색광고 키 없음) 경쟁만으로 매긴다.
    /// 상위 글 중 제목에 키워드를 정확히 쓴 글이 몇 개인지가 핵심이다.
    /// </summary>
    private static string Grade(NaverBlogSearchService.BlogSearchResult s, int? monthly)
    {
        var hits = s.ExactTitleMatches;
        if (monthly is null)
            return hits switch
            {
                0 => "빈자리",
                <= 2 => "여지있음",
                <= 5 => "보통",
                _ => "포화"
            };

        var m = monthly.Value;
        if (m < 100) return "수요적음";
        return hits switch
        {
            0 when m >= 500 => "노려볼만함",
            <= 2 when m >= 200 => "해볼만함",
            <= 5 => "보통",
            _ => "어려움"
        };
    }

    // ── 저장 ──────────────────────────────────────────────────────
    private static async Task<long> InsertSeedAsync(SqliteConnection c, string seed, string now, CancellationToken ct)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO KeywordSeeds (SeedKeyword, RequestedAt, ResultCount)
            VALUES ($s, $t, 0);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$s", seed);
        cmd.Parameters.AddWithValue("$t", now);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task UpdateSeedCountAsync(SqliteConnection c, long seedId, int n, CancellationToken ct)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE KeywordSeeds SET ResultCount = $n WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$n", n);
        cmd.Parameters.AddWithValue("$id", seedId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>키워드는 중복 없이 한 줄. 이미 있으면 마지막 확인 시각만 갱신한다.</summary>
    private static async Task<long> UpsertKeywordAsync(
        SqliteConnection c, string keyword, long seedId, string now, CancellationToken ct)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Keywords (Keyword, SourceSeedId, FirstSeenAt, LastCheckedAt, Status)
            VALUES ($k, $s, $t, $t, '후보')
            ON CONFLICT(Keyword) DO UPDATE SET LastCheckedAt = $t;
            SELECT Id FROM Keywords WHERE Keyword = $k;
            """;
        cmd.Parameters.AddWithValue("$k", keyword);
        cmd.Parameters.AddWithValue("$s", seedId);
        cmd.Parameters.AddWithValue("$t", now);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task InsertMetricAsync(
        SqliteConnection c, long keywordId, string now,
        NaverSearchAdService.KeywordVolume? vol,
        NaverBlogSearchService.BlogSearchResult search,
        string tier, double? score, string grade, CancellationToken ct)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO KeywordMetrics
                (KeywordId, MeasuredAt, NaverPc, NaverMobile, NaverCompetition,
                 BlogTotalCount, Analyzed, ExactTitleMatches, ExactAnyMatches,
                 LatestPostDate, VolumeTier, OpportunityScore, Grade, TopPostsJson)
            VALUES ($id, $t, $pc, $mo, $comp, $blog, $an, $et, $ea, $latest, $tier, $score, $grade, $posts);
            """;
        cmd.Parameters.AddWithValue("$id", keywordId);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.Parameters.AddWithValue("$pc", (object?)vol?.MonthlyPc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mo", (object?)vol?.MonthlyMobile ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$comp", (object?)vol?.Competition ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$blog", search.TotalCount);
        cmd.Parameters.AddWithValue("$an", search.Analyzed);
        cmd.Parameters.AddWithValue("$et", search.ExactTitleMatches);
        cmd.Parameters.AddWithValue("$ea", search.ExactAnyMatches);
        cmd.Parameters.AddWithValue("$latest", (object?)search.LatestPostDate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tier", tier);
        cmd.Parameters.AddWithValue("$score", (object?)score ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$grade", grade);
        cmd.Parameters.AddWithValue("$posts", JsonSerializer.Serialize(search.TopPosts));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task EnsureTablesAsync(SqliteConnection c, CancellationToken ct)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = """
            -- 어떤 시드로 언제 조사했나
            CREATE TABLE IF NOT EXISTS KeywordSeeds (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SeedKeyword TEXT NOT NULL,
                RequestedAt TEXT NOT NULL,
                ResultCount INTEGER NOT NULL DEFAULT 0,
                Note TEXT
            );

            -- 키워드 마스터. Status로 글 작성 진행을 관리한다.
            CREATE TABLE IF NOT EXISTS Keywords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Keyword TEXT NOT NULL UNIQUE,
                SourceSeedId INTEGER,
                Category TEXT,
                Memo TEXT,
                Status TEXT NOT NULL DEFAULT '후보',   -- 후보 / 작성예정 / 작성완료 / 제외
                FirstSeenAt TEXT NOT NULL,
                LastCheckedAt TEXT NOT NULL
            );

            -- 측정값 시계열. 같은 키워드를 다시 재면 행이 쌓여 추이가 된다.
            CREATE TABLE IF NOT EXISTS KeywordMetrics (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                KeywordId INTEGER NOT NULL,
                MeasuredAt TEXT NOT NULL,
                NaverPc INTEGER,
                NaverMobile INTEGER,
                NaverCompetition TEXT,                       -- 광고 경쟁도. 블로그 경쟁과는 다른 값이다
                BlogTotalCount INTEGER NOT NULL DEFAULT 0,   -- 네이버 보고 건수(느슨한 매칭, 참고용)
                Analyzed INTEGER NOT NULL DEFAULT 0,         -- 정확도 계산에 쓴 상위 글 수
                ExactTitleMatches INTEGER NOT NULL DEFAULT 0,-- 그중 제목에 키워드가 그대로 있는 글 = 실질 경쟁
                ExactAnyMatches INTEGER NOT NULL DEFAULT 0,
                LatestPostDate TEXT,                         -- 가장 최근 글 날짜. 오래됐으면 아무도 안 쓰는 주제다
                VolumeTier TEXT,                             -- A-대형 / B-중형 / C-롱테일
                OpportunityScore REAL,
                Grade TEXT,
                GoogleTrendIndex INTEGER,
                TopPostsJson TEXT NOT NULL DEFAULT '[]',
                FOREIGN KEY (KeywordId) REFERENCES Keywords(Id)
            );
            CREATE INDEX IF NOT EXISTS IX_KeywordMetrics_Keyword ON KeywordMetrics(KeywordId, MeasuredAt);
            CREATE INDEX IF NOT EXISTS IX_KeywordMetrics_Tier ON KeywordMetrics(VolumeTier, OpportunityScore);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
