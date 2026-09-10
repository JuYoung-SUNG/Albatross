using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Albatross.Collector.News.Services;

/// <summary>
/// 블로그에 쓸 키워드를 조사한다.
///
///   시드 키워드 하나 입력
///     → 자동완성으로 연관 키워드 확장 (키 불필요)
///     → 검색광고 API로 월간 검색수 (키 있을 때만)
///     → 블로그 검색 API로 누적 문서수 = 경쟁, 그리고 현재 상위 글
///     → 기회 점수 계산 후 저장
///
/// 저장 구조를 3장으로 나눈 이유:
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

    public sealed record Options(
        int ExpandDepth = 1,
        int MaxKeywords = 40,
        int MinMonthlySearch = 0,   // 검색광고 키가 없으면 검색량을 모르므로 기본 0
        int TopPosts = 5);

    /// <summary>시드 하나를 조사해 DB에 저장한다. 저장된 키워드 수를 반환.</summary>
    public async Task<int> ResearchAsync(string databasePath, string seed, Options opt, CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={databasePath}");
        await conn.OpenAsync(ct);
        await EnsureTablesAsync(conn, ct);

        var now = DateTimeOffset.Now.ToString("O");
        _logger.LogInformation("[키워드조사] '{seed}' 시작", seed);

        // 1) 후보 넓히기
        var candidates = await _autocomplete.ExpandAsync(seed, opt.ExpandDepth, opt.MaxKeywords, ct);
        if (candidates.Count == 0)
        {
            _logger.LogWarning("[키워드조사] '{seed}' — 후보를 얻지 못했습니다", seed);
            return 0;
        }

        // 2) 월간 검색수 (키가 없으면 빈 사전이 온다 — 나머지 단계는 그대로 진행)
        var volumes = await _searchAd.GetVolumesAsync(candidates, includeRelated: false, ct);
        if (volumes.Count == 0)
            _logger.LogWarning("[키워드조사] 월간 검색수를 얻지 못했습니다 (NAVER_AD_* 미설정이면 정상)");

        // 3) 시드 이력 기록
        var seedId = await InsertSeedAsync(conn, seed, now, ct);

        // 4) 키워드별 경쟁 조사 후 저장
        var saved = 0;
        foreach (var kw in candidates)
        {
            ct.ThrowIfCancellationRequested();

            volumes.TryGetValue(kw, out var vol);
            var monthly = vol?.MonthlyTotal ?? 0;

            // 검색량을 아는데 하한 미달이면 블로그 API를 부르지 않고 건너뛴다 (호출 절약)
            if (vol is not null && monthly < opt.MinMonthlySearch) continue;

            var search = await _blog.SearchAsync(kw, opt.TopPosts, ct);
            if (search is null) continue;

            // 경쟁은 total이 아니라 "제목에 정확히 그 키워드를 쓴 글 수"로 본다
            var score = vol is null ? (double?)null : CalcScore(monthly, search.ExactTitleMatches, search.Analyzed);
            var grade = Grade(search, vol?.MonthlyTotal);

            var keywordId = await UpsertKeywordAsync(conn, kw, seedId, now, ct);
            await InsertMetricAsync(conn, keywordId, now, vol, search, score, grade, ct);
            saved++;

            await Task.Delay(120, ct);   // 검색 API 초당 호출 제한 회피
        }

        await UpdateSeedCountAsync(conn, seedId, saved, ct);
        _logger.LogInformation("[키워드조사] '{seed}' 완료 — 키워드 {n}개 저장", seed, saved);
        return saved;
    }

    /// <summary>
    /// 기회 점수 = 월간 검색수 ÷ (제목에 그 키워드를 정확히 쓴 글의 비율).
    /// 상위 10개 중 정확히 겨냥한 글이 적을수록 비집고 들어갈 자리가 크다.
    /// </summary>
    private static double CalcScore(int monthlySearch, int exactTitle, int analyzed)
    {
        if (analyzed <= 0) return 0;
        var occupied = (exactTitle + 0.5) / analyzed;   // 0이어도 무한대가 되지 않도록 보정
        return Math.Round(monthlySearch / occupied / 1000.0, 3);
    }

    /// <summary>
    /// 등급. 검색량을 모르면(검색광고 키 없음) 경쟁만으로 매긴다.
    /// 상위 10개 중 제목에 키워드를 정확히 쓴 글이 몇 개인지가 핵심이다.
    /// </summary>
    private static string Grade(NaverBlogSearchService.BlogSearchResult s, int? monthly)
    {
        var hits = s.ExactTitleMatches;
        if (monthly is null)
            return hits switch
            {
                0 => "빈자리",          // 아무도 정확히 겨냥하지 않음
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
        double? score, string grade, CancellationToken ct)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO KeywordMetrics
                (KeywordId, MeasuredAt, NaverPc, NaverMobile, NaverCompetition,
                 BlogTotalCount, Analyzed, ExactTitleMatches, ExactAnyMatches,
                 LatestPostDate, OpportunityScore, Grade, TopPostsJson)
            VALUES ($id, $t, $pc, $mo, $comp, $blog, $an, $et, $ea, $latest, $score, $grade, $posts);
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
                NaverCompetition TEXT,
                BlogTotalCount INTEGER NOT NULL DEFAULT 0,   -- 네이버 보고 건수(느슨한 매칭, 참고용)
                Analyzed INTEGER NOT NULL DEFAULT 0,         -- 정확도 계산에 쓴 상위 글 수
                ExactTitleMatches INTEGER NOT NULL DEFAULT 0,-- 그중 제목에 키워드가 그대로 있는 글 = 실질 경쟁
                ExactAnyMatches INTEGER NOT NULL DEFAULT 0,
                LatestPostDate TEXT,                         -- 가장 최근 글 날짜. 오래됐으면 아무도 안 쓰는 주제다
                OpportunityScore REAL,
                Grade TEXT,
                GoogleTrendIndex INTEGER,
                TopPostsJson TEXT NOT NULL DEFAULT '[]',
                FOREIGN KEY (KeywordId) REFERENCES Keywords(Id)
            );
            CREATE INDEX IF NOT EXISTS IX_KeywordMetrics_Keyword ON KeywordMetrics(KeywordId, MeasuredAt);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
