using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Albatross.Collector.News.Services;

/// <summary>
/// "이 주제로 글을 쓰면 될 것 같다" 목록을 만든다.
///
///   NewsKeywords(뉴스에서 뽑은 키워드)
///     → 검색광고 API로 월간 검색수·경쟁정도
///     → 블로그 검색 API로 누적 문서 수(경쟁)와 현재 상위 글
///     → 기회 점수 계산 후 KeywordOpportunities에 저장
///
/// 핵심 지표는 "검색수 ÷ 블로그 문서수"다.
/// 검색하는 사람은 많은데 쓴 글이 적을수록 새로 써서 상위에 갈 여지가 크다.
/// 검색량만 보면 경쟁이 살인적인 키워드를 고르게 되고,
/// 문서수만 보면 아무도 안 찾는 키워드를 고르게 된다.
/// </summary>
public class KeywordOpportunityService
{
    private readonly NaverSearchAdService _searchAd;
    private readonly NaverBlogSearchService _blog;
    private readonly ILogger<KeywordOpportunityService> _logger;

    public KeywordOpportunityService(
        NaverSearchAdService searchAd, NaverBlogSearchService blog, ILogger<KeywordOpportunityService> logger)
    {
        _searchAd = searchAd;
        _blog = blog;
        _logger = logger;
    }

    /// <summary>글감으로서의 매력도를 4단계로 나눈다. 사이트에서 그대로 뱃지로 쓴다.</summary>
    public enum Grade { 노려볼만함, 해볼만함, 보통, 어려움 }

    public sealed record Opportunity(
        string Keyword,
        string Category,
        string? Gist,
        int MonthlySearchPc,
        int MonthlySearchMobile,
        string Competition,
        int BlogTotalCount,
        double OpportunityScore,
        Grade Grade,
        List<NaverBlogSearchService.BlogPost> TopPosts,
        List<string> SampleTitles)
    {
        public int MonthlySearchTotal => MonthlySearchPc + MonthlySearchMobile;
    }

    /// <summary>
    /// 최근 며칠간의 NewsKeywords를 대상으로 전체 파이프라인을 돌린다.
    /// </summary>
    /// <param name="minSearchVolume">이보다 검색이 적으면 글을 써도 볼 사람이 없어 제외</param>
    /// <param name="maxKeywords">API 호출량 통제를 위한 상한</param>
    public async Task<int> AnalyzeAndSaveAsync(
        string databasePath, int days, int minSearchVolume, int maxKeywords, bool includeRelated, CancellationToken ct)
    {
        if (!_searchAd.IsConfigured)
        {
            _logger.LogError("[기회] 검색광고 API 자격증명(NAVER_AD_*)이 없어 진행할 수 없습니다");
            return 0;
        }

        await using var conn = new SqliteConnection($"Data Source={databasePath}");
        await conn.OpenAsync(ct);
        await EnsureTableAsync(conn, ct);

        // 1) 뉴스에서 뽑아둔 키워드 불러오기
        var seeds = await LoadSeedKeywordsAsync(conn, days, maxKeywords, ct);
        if (seeds.Count == 0)
        {
            _logger.LogWarning("[기회] 최근 {d}일 NewsKeywords가 비어 있습니다. 먼저 --extract-keywords 를 실행하세요", days);
            return 0;
        }
        _logger.LogInformation("[기회] 후보 키워드 {n}개로 시작", seeds.Count);

        // 2) 월간 검색수 조회 (연관 키워드까지 받으면 후보가 늘어난다)
        var volumes = await _searchAd.GetVolumesAsync(seeds.Keys, includeRelated, ct);
        if (volumes.Count == 0)
        {
            _logger.LogError("[기회] 검색수를 하나도 가져오지 못했습니다 — 자격증명이나 네트워크를 확인하세요");
            return 0;
        }

        // 3) 검색량이 너무 적은 키워드는 여기서 잘라낸다 (블로그 API 호출을 아끼는 효과도 있다)
        var worth = volumes.Values
            .Where(v => v.MonthlyTotal >= minSearchVolume)
            .OrderByDescending(v => v.MonthlyTotal)
            .Take(maxKeywords)
            .ToList();
        _logger.LogInformation("[기회] 월 검색 {min}회 이상 {n}개로 압축 (전체 {all}개 중)",
            minSearchVolume, worth.Count, volumes.Count);

        // 4) 각 키워드의 블로그 경쟁 상황 확인
        var opportunities = new List<Opportunity>();
        foreach (var v in worth)
        {
            ct.ThrowIfCancellationRequested();
            var search = await _blog.SearchAsync(v.Keyword, 5, ct);
            if (search == null) continue;

            var score = CalcScore(v.MonthlyTotal, search.TotalCount);
            seeds.TryGetValue(v.Keyword, out var seed);

            opportunities.Add(new Opportunity(
                v.Keyword,
                seed?.Category ?? "연관",
                seed?.Gist,
                v.MonthlyPc,
                v.MonthlyMobile,
                v.Competition,
                search.TotalCount,
                score,
                ToGrade(score, v.MonthlyTotal),
                search.TopPosts,
                seed?.SampleTitles ?? new List<string>()));

            await Task.Delay(120, ct);   // 검색 API 초당 호출 제한 회피
        }

        // 5) 저장 — 좋은 것부터
        opportunities = opportunities.OrderByDescending(o => o.OpportunityScore).ToList();
        await SaveAsync(conn, opportunities, ct);

        var good = opportunities.Count(o => o.Grade is Grade.노려볼만함 or Grade.해볼만함);
        _logger.LogInformation("[기회] {n}개 분석 완료 — 그중 쓸 만한 키워드 {g}개", opportunities.Count, good);
        return opportunities.Count;
    }

    /// <summary>
    /// 기회 점수 = 월간 검색수 ÷ 블로그 누적 문서수.
    /// 1.0이면 "글 1개당 검색 1회" 수준이라는 뜻으로, 클수록 수요 대비 공급이 부족하다.
    /// </summary>
    private static double CalcScore(int monthlySearch, int blogTotal) =>
        Math.Round(monthlySearch / (double)Math.Max(blogTotal, 1), 4);

    /// <summary>
    /// 점수만으로 등급을 매기면 "검색 30회 / 문서 5개" 같은 의미 없는 키워드가 1등이 된다.
    /// 그래서 최소 검색량 조건을 함께 본다.
    /// </summary>
    private static Grade ToGrade(double score, int monthlySearch) => score switch
    {
        >= 1.0 when monthlySearch >= 500 => Grade.노려볼만함,
        >= 0.3 when monthlySearch >= 200 => Grade.해볼만함,
        >= 0.1 => Grade.보통,
        _ => Grade.어려움
    };

    private sealed record Seed(string Category, string? Gist, List<string> SampleTitles);

    private async Task<Dictionary<string, Seed>> LoadSeedKeywordsAsync(
        SqliteConnection conn, int days, int cap, CancellationToken ct)
    {
        var result = new Dictionary<string, Seed>(StringComparer.OrdinalIgnoreCase);

        // 최근 N일에 키워드가 없으면(수집이 멈춰 있는 등) 가장 최근 회차라도 쓴다.
        var recent = conn.CreateCommand();
        recent.CommandText = "SELECT COUNT(*) FROM NewsKeywords WHERE date(WindowStart) >= date('now', $days);";
        recent.Parameters.AddWithValue("$days", $"-{days} day");
        var hasRecent = Convert.ToInt32(await recent.ExecuteScalarAsync(ct)) > 0;

        var cmd = conn.CreateCommand();
        cmd.CommandText = hasRecent
            ? """
              SELECT Keyword, Category, Gist, SampleTitles
              FROM NewsKeywords
              WHERE date(WindowStart) >= date('now', $days)
              ORDER BY Score DESC
              LIMIT $cap;
              """
            : """
              SELECT Keyword, Category, Gist, SampleTitles
              FROM NewsKeywords
              WHERE date(WindowStart) = (SELECT MAX(date(WindowStart)) FROM NewsKeywords)
              ORDER BY Score DESC
              LIMIT $cap;
              """;
        cmd.Parameters.AddWithValue("$days", $"-{days} day");
        cmd.Parameters.AddWithValue("$cap", cap);
        if (!hasRecent)
            _logger.LogWarning("[기회] 최근 {d}일 키워드가 없어 가장 최근 추출분을 사용합니다", days);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var kw = r.GetString(0);
            if (result.ContainsKey(kw)) continue;
            var titles = new List<string>();
            if (!r.IsDBNull(3))
            {
                try { titles = JsonSerializer.Deserialize<List<string>>(r.GetString(3)) ?? new(); }
                catch { /* 저장 형식이 깨졌으면 제목 없이 진행 */ }
            }
            result[kw] = new Seed(r.IsDBNull(1) ? "미분류" : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), titles);
        }
        return result;
    }

    private static async Task EnsureTableAsync(SqliteConnection conn, CancellationToken ct)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS KeywordOpportunities (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Keyword TEXT NOT NULL,
                Category TEXT,
                Gist TEXT,
                MonthlySearchPc INTEGER NOT NULL DEFAULT 0,
                MonthlySearchMobile INTEGER NOT NULL DEFAULT 0,
                Competition TEXT,
                BlogTotalCount INTEGER NOT NULL DEFAULT 0,
                OpportunityScore REAL NOT NULL DEFAULT 0,
                Grade TEXT,
                TopPostsJson TEXT NOT NULL DEFAULT '[]',
                SampleTitlesJson TEXT NOT NULL DEFAULT '[]',
                AnalyzedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_KeywordOpportunities_Analyzed
                ON KeywordOpportunities(AnalyzedAt);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>같은 날 분석분은 지우고 새로 넣어, 여러 번 돌려도 중복이 쌓이지 않게 한다.</summary>
    private async Task SaveAsync(SqliteConnection conn, List<Opportunity> items, CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        await using var tx = await conn.BeginTransactionAsync(ct);

        var del = conn.CreateCommand();
        del.Transaction = (SqliteTransaction)tx;
        del.CommandText = "DELETE FROM KeywordOpportunities WHERE date(AnalyzedAt) = date($now);";
        del.Parameters.AddWithValue("$now", now.ToString("O"));
        await del.ExecuteNonQueryAsync(ct);

        foreach (var o in items)
        {
            var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO KeywordOpportunities
                    (Keyword, Category, Gist, MonthlySearchPc, MonthlySearchMobile, Competition,
                     BlogTotalCount, OpportunityScore, Grade, TopPostsJson, SampleTitlesJson, AnalyzedAt)
                VALUES ($kw, $cat, $gist, $pc, $mo, $comp, $blog, $score, $grade, $posts, $titles, $at);
                """;
            cmd.Parameters.AddWithValue("$kw", o.Keyword);
            cmd.Parameters.AddWithValue("$cat", o.Category);
            cmd.Parameters.AddWithValue("$gist", (object?)o.Gist ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pc", o.MonthlySearchPc);
            cmd.Parameters.AddWithValue("$mo", o.MonthlySearchMobile);
            cmd.Parameters.AddWithValue("$comp", o.Competition);
            cmd.Parameters.AddWithValue("$blog", o.BlogTotalCount);
            cmd.Parameters.AddWithValue("$score", o.OpportunityScore);
            cmd.Parameters.AddWithValue("$grade", o.Grade.ToString());
            cmd.Parameters.AddWithValue("$posts", JsonSerializer.Serialize(o.TopPosts));
            cmd.Parameters.AddWithValue("$titles", JsonSerializer.Serialize(o.SampleTitles));
            cmd.Parameters.AddWithValue("$at", now.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        _logger.LogInformation("[기회] KeywordOpportunities에 {n}건 저장", items.Count);
    }
}
