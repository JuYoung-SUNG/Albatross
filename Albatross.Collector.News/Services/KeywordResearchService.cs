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
    /// 검색량 구간. 10배 단위(로그 스케일)로 나눈다.
    ///
    /// 처음엔 3구간(10만+ / 1천~10만 / 100~1천)이었는데 가운데 칸이 100배 폭이라
    /// 월 1,200회와 95,000회가 한 칸에 들어갔다. 실제 분포를 보니 1천~1만(508개)과
    /// 1만~10만(131개)이 성격이 전혀 달라서 갈랐다.
    ///
    ///   S-초대형  100만+     포털 자체 서비스가 답을 줘서 블로그가 끼기 거의 불가능
    ///   A-대형    10만~100만  경쟁 심함. 자리 잡은 블로그가 노릴 구간
    ///   B-중대형  1만~10만   승산 있고 유입도 의미 있는 구간
    ///   C-중형    1천~1만    시작하는 블로그의 현실적인 목표
    ///   D-롱테일  100~1천    쉽지만 여러 개를 모아야 유입이 됨
    /// </summary>
    public static string TierOf(int monthly) => monthly switch
    {
        >= 1_000_000 => "S-초대형",
        >= 100_000 => "A-대형",
        >= 10_000 => "B-중대형",
        >= 1_000 => "C-중형",
        >= 100 => "D-롱테일",
        _ => "E-미미"
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
    /// 3단계 트리로 조사한다.
    ///
    ///   L1 대분류  시드 그대로 (주식, 금리, …)
    ///   L2 중분류  검색광고 연관어 상위 N개 (주식 → 코스피지수, 삼성전자주가, 나스닥)
    ///   L3 소분류  자동완성 (코스피지수 → 코스피지수 뜻, 코스피지수 etf, 오늘코스피지수)
    ///
    /// 단계마다 다른 API를 쓰는 이유 —
    ///   L2를 자동완성으로 뽑으면 "주식투자", "주식시세"처럼 시드를 살짝 바꾼 말만 나와 넓어지지 않는다.
    ///   반대로 L3를 검색광고 연관어로 뽑으면 "코스피지수 → 루닛주가, 공모주"처럼
    ///   하위가 아니라 옆 키워드가 나와 트리가 그물이 된다. 실제로 확인하고 갈랐다.
    ///
    /// 블로그 경쟁 조사는 L2·L3만 한다. L1은 분류 이름일 뿐 글을 쓸 대상이 아니다.
    /// </summary>
    public async Task<int> ResearchTreeAsync(
        string databasePath, List<string> roots, int perRoot, int perBranch,
        Options opt, CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={databasePath}");
        await conn.OpenAsync(ct);
        await EnsureTablesAsync(conn, ct);

        var now = DateTimeOffset.Now.ToString("O");
        _logger.LogInformation("[트리조사] 대분류 {n}개 시작 — L2 각 {a}개, L3 각 {b}개",
            roots.Count, perRoot, perBranch);

        var seedId = await InsertSeedAsync(conn, "트리: " + string.Join(", ", roots), now, ct);
        var saved = 0;

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();

            // L1 — 분류 이름. 측정은 하지 않고 뼈대만 만든다.
            var rootId = await UpsertTreeKeywordAsync(conn, root, null, root, 1, seedId, now, ct);

            // L2 — 검색광고 연관어에서 검색량 큰 순으로
            var l2Volumes = await _searchAd.GetVolumesAsync(new[] { root }, includeRelated: true, ct);
            var l2 = l2Volumes.Values
                .Where(v => v.MonthlyTotal >= 1000 && !Same(v.Keyword, root))
                .OrderByDescending(v => v.MonthlyTotal)
                .Take(perRoot)
                .ToList();
            _logger.LogInformation("[트리조사] {root} → 중분류 {n}개", root, l2.Count);

            foreach (var mid in l2)
            {
                ct.ThrowIfCancellationRequested();
                var midId = await UpsertTreeKeywordAsync(conn, mid.Keyword, rootId, root, 2, seedId, now, ct);
                if (await MeasureAsync(conn, midId, mid, now, opt, ct)) saved++;

                // L3 — 자동완성. 부모를 포함한 구체적인 질문이 나온다.
                var suggestions = await _autocomplete.SuggestAsync(mid.Keyword, ct);
                var children = suggestions
                    .Where(s => !Same(s, mid.Keyword) && !Same(s, root))
                    .Take(perBranch)
                    .ToList();
                if (children.Count == 0) continue;

                // 자식들의 검색량을 한 번에 조회 (연관어는 받지 않는다 — 트리가 흐트러진다)
                var childVolumes = await _searchAd.GetVolumesAsync(children, includeRelated: false, ct);

                foreach (var child in children)
                {
                    ct.ThrowIfCancellationRequested();
                    childVolumes.TryGetValue(child, out var vol);
                    if (vol is null || vol.MonthlyTotal < 100) continue;   // 볼 사람이 없는 말은 버린다

                    var childId = await UpsertTreeKeywordAsync(conn, child, midId, root, 3, seedId, now, ct);
                    if (await MeasureAsync(conn, childId, vol, now, opt, ct)) saved++;
                }
            }
        }

        await UpdateSeedCountAsync(conn, seedId, saved, ct);
        _logger.LogInformation("[트리조사] 완료 — 측정 {n}건 저장", saved);
        return saved;
    }


    /// <summary>
    /// 이미 만들어진 트리의 키워드를 다시 잰다. 새로 발굴하지 않는다.
    ///
    /// 주간 갱신이 이걸 쓴다 — 트리 구조(어떤 중분류 아래 어떤 소분류가 있는지)는 자주 바뀌지 않지만
    /// 경쟁(제목 일치·최신 글)은 새 글이 올라오며 매일 바뀐다. 구조를 다시 파면 15분이 걸리는데,
    /// 바뀌는 값만 재면 그 절반이면 끝나고 시계열도 끊기지 않는다.
    ///
    /// 검색량도 함께 새로 받는다 — 네이버가 월평균을 갱신하므로 값이 조금씩 움직인다.
    /// </summary>
    /// <param name="levels">다시 잴 단계. 기본은 중분류·소분류 둘 다.</param>
    public async Task<int> RemeasureAsync(
        string databasePath, int[] levels, int maxCount, Options opt, CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={databasePath}");
        await conn.OpenAsync(ct);
        await EnsureTablesAsync(conn, ct);

        var targets = new List<(long Id, string Keyword)>();
        var sel = conn.CreateCommand();
        sel.CommandText = $"""
            SELECT Id, Keyword FROM Keywords
            WHERE TreeLevel IN ({string.Join(",", levels)})
            ORDER BY LastCheckedAt
            LIMIT $max;
            """;
        sel.Parameters.AddWithValue("$max", maxCount);
        await using (var r = await sel.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) targets.Add((r.GetInt64(0), r.GetString(1)));

        if (targets.Count == 0)
        {
            _logger.LogWarning("[재측정] 대상이 없습니다. 먼저 --tree 로 트리를 만드세요");
            return 0;
        }

        var now = DateTimeOffset.Now.ToString("O");
        _logger.LogInformation("[재측정] {n}개 시작 (단계 {lv})", targets.Count, string.Join("·", levels));
        var seedId = await InsertSeedAsync(conn, $"재측정 {targets.Count}개", now, ct);

        // 검색량은 5개씩 묶어 받는 편이 호출이 적다. 연관어는 받지 않는다 — 트리를 흐트러뜨리지 않기 위해서다.
        var volumes = await _searchAd.GetVolumesAsync(targets.Select(t => t.Keyword), includeRelated: false, ct);

        var saved = 0;
        foreach (var (id, keyword) in targets)
        {
            ct.ThrowIfCancellationRequested();
            if (!volumes.TryGetValue(keyword, out var vol)) continue;   // 검색량을 못 받으면 건너뛴다

            if (await MeasureAsync(conn, id, vol, now, opt, ct))
            {
                saved++;
                var upd = conn.CreateCommand();
                upd.CommandText = "UPDATE Keywords SET LastCheckedAt = $t WHERE Id = $id;";
                upd.Parameters.AddWithValue("$t", now);
                upd.Parameters.AddWithValue("$id", id);
                await upd.ExecuteNonQueryAsync(ct);
            }

            if (saved % 50 == 0 && saved > 0)
                _logger.LogInformation("[재측정]   진행 {n}/{all}", saved, targets.Count);
        }

        await UpdateSeedCountAsync(conn, seedId, saved, ct);
        _logger.LogInformation("[재측정] 완료 — {n}건 갱신", saved);
        return saved;
    }
    /// <summary>블로그 경쟁을 재서 KeywordMetrics에 한 행 남긴다.</summary>
    private async Task<bool> MeasureAsync(
        SqliteConnection conn, long keywordId, NaverSearchAdService.KeywordVolume vol,
        string now, Options opt, CancellationToken ct)
    {
        var search = await _blog.SearchAsync(vol.Keyword, opt.TopPosts, ct);
        if (search is null) return false;

        var tier = TierOf(vol.MonthlyTotal);
        var score = CalcScore(vol.MonthlyTotal, search.ExactTitleMatches, search.Analyzed);
        var grade = Grade(search, vol.MonthlyTotal);
        await InsertMetricAsync(conn, keywordId, now, vol, search, tier, score, grade, ct);

        await Task.Delay(120, ct);   // 검색 API 초당 호출 제한 회피
        return true;
    }

    /// <summary>공백만 다른 키워드를 같은 것으로 본다 ("엔화 환율" vs "엔화환율").</summary>
    private static bool Same(string a, string b) =>
        string.Equals(a.Replace(" ", ""), b.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 트리 위치까지 함께 저장한다. 이미 있는 키워드면 위치를 갱신한다 —
    /// 같은 말이 여러 분류에 걸칠 때는 마지막에 본 위치를 따른다.
    /// </summary>
    private static async Task<long> UpsertTreeKeywordAsync(
        SqliteConnection c, string keyword, long? parentId, string root, int level,
        long seedId, string now, CancellationToken ct)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Keywords (Keyword, SourceSeedId, FirstSeenAt, LastCheckedAt, Status,
                                  ParentId, RootKeyword, TreeLevel)
            VALUES ($k, $s, $t, $t, '후보', $p, $r, $l)
            ON CONFLICT(Keyword) DO UPDATE SET
                LastCheckedAt = $t, ParentId = $p, RootKeyword = $r, TreeLevel = $l;
            SELECT Id FROM Keywords WHERE Keyword = $k;
            """;
        cmd.Parameters.AddWithValue("$k", keyword);
        cmd.Parameters.AddWithValue("$s", seedId);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.Parameters.AddWithValue("$p", (object?)parentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$r", root);
        cmd.Parameters.AddWithValue("$l", level);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
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
                LastCheckedAt TEXT NOT NULL,
                -- 트리 구조: L1 대분류(시드) → L2 중분류(검색광고 연관어) → L3 소분류(자동완성)
                ParentId INTEGER,      -- 바로 위 단계의 Keywords.Id
                RootKeyword TEXT,      -- L1 이름. 화면 탭을 이걸로 가른다
                TreeLevel INTEGER NOT NULL DEFAULT 0   -- 1 / 2 / 3, 0은 트리 이전에 수집된 것
            );
            -- IX_Keywords_Tree 는 아래 ALTER 로 컬럼을 붙인 뒤에 만든다.
            -- 여기서 만들면 기존 DB에는 아직 컬럼이 없어 이 배치 전체가 실패하고
            -- 뒤따르는 ALTER 가 아예 실행되지 않는다 (실제로 그렇게 막혔다).

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

        // 이미 만들어진 DB에는 CREATE TABLE이 적용되지 않으므로 컬럼을 따로 붙인다.
        // 있으면 예외가 나는데 그게 정상이라 삼킨다 (SQLite에 ADD COLUMN IF NOT EXISTS가 없다).
        foreach (var ddl in new[]
        {
            "ALTER TABLE Keywords ADD COLUMN ParentId INTEGER",
            "ALTER TABLE Keywords ADD COLUMN RootKeyword TEXT",
            "ALTER TABLE Keywords ADD COLUMN TreeLevel INTEGER NOT NULL DEFAULT 0"
        })
        {
            try { var a = c.CreateCommand(); a.CommandText = ddl; await a.ExecuteNonQueryAsync(ct); }
            catch (SqliteException) { /* 이미 있음 */ }
        }

        var idx = c.CreateCommand();
        idx.CommandText = "CREATE INDEX IF NOT EXISTS IX_Keywords_Tree ON Keywords(RootKeyword, TreeLevel, ParentId);";
        await idx.ExecuteNonQueryAsync(ct);
    }
}
