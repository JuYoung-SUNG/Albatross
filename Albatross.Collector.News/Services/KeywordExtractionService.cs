using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Albatross.Collector.News.Services;

/// <summary>
/// RawNews(제목+요약)에서 "지금 뜨는" 블로그 소재 키워드를 뽑는 하이브리드 추출기.
///  1단계(통계): 대상 기간의 명사형 토큰 빈도 + 직전 기준선 대비 급상승(spike)으로 후보를 추린다.
///  2단계(Gemma): 후보 + 대표 제목을 로컬 Gemma에 넣어 사람이 읽기 좋은 키워드/요지/관련기사로 정제한다.
///  3단계(선택, 추후): 네이버 DataLab 검색량으로 크로스체크.
/// 결과는 NewsKeywords 테이블에 시간대(윈도우)별로 축적한다.
/// </summary>
public class KeywordExtractionService
{
    private readonly GemmaClassificationService _gemma;
    private readonly NaverDataLabService _dataLab;
    private readonly ILogger<KeywordExtractionService> _logger;

    public KeywordExtractionService(GemmaClassificationService gemma, NaverDataLabService dataLab, ILogger<KeywordExtractionService> logger)
    {
        _gemma = gemma;
        _dataLab = dataLab;
        _logger = logger;
    }

    // 뉴스에서 흔하지만 소재로는 의미 없는 말들 (조사 제거 후 기준). 필요 시 계속 보강.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "기자","뉴스","오늘","내일","어제","올해","작년","지난해","지난","이번","최근","가운데","관련","위해","통해","대해","대한",
        "이날","우리","사진","영상","속보","단독","종합","이상","이하","그동안","지금","당시","경우","모습","상황","계획","예정",
        "진행","발표","공개","확인","공식","전망","분석","입장","의혹","논란","파문","연합뉴스","보도","취재","제공","무단","배포",
        "재배포","금지","저작권","앵커","리포트","다음","이후","이전","현재","전날","당일","하루","이틀","사흘","여러","각각","서로",
        "정도","때문","관계자","측은","측에","대비","기준","포함","제외","가능","불가","여부","등을","등이","등의","등에","것으로",
        "밝혔다","말했다","전했다","나타났다","드러났다","알려졌다","것이다","한편","그러나","하지만","또한","특히","이에","이런",
        "그런","저런","무슨","어떤","모든","일부","전체","관련해","위한","향한","대상","중심","기록","수준","규모","효과","방안",
        "the","and","for","with","from","that","this","new","says","after","over","한국","서울"
    };

    // 뒤에 자주 붙는 조사/어미 (긴 것부터 제거해야 정확). 제거 후 어근 길이 2 이상일 때만 적용.
    private static readonly string[] JosaSuffixes =
    {
        "으로서","으로써","에게서","이라고","라고는","에서는","에서도","으로는","으로도","까지도","부터는",
        "에게는","에게도","이라는","라는","께서는","이라며","라며는","이라며",
        "에서","에게","으로","까지","부터","보다","처럼","마다","조차","한테","께서","이라","라며","라고",
        "은","는","이","가","을","를","의","에","와","과","도","만","랑","나","고","며","야","여",
        "께","로","서","이나","이란","이든"
    };

    // 한글 2자 이상 덩어리, 또는 영문/숫자 조합 2자 이상(KTX, CPTPP, AI, K리그 등)
    private static readonly Regex TokenRegex = new(@"[가-힣]{2,}|[A-Za-z][A-Za-z0-9]{1,}|[A-Za-z0-9]*[가-힣]+[A-Za-z0-9]+", RegexOptions.Compiled);

    public sealed record KeywordCandidate(
        string Term,
        int Frequency,
        double SpikeRatio,
        double Score,
        string Category,
        List<string> RelatedNewsIds,
        List<string> SampleTitles);

    private sealed record NewsRow(string Id, string Title, string Summary, string Category);

    /// <summary>
    /// 대상 윈도우(currentStart~currentEnd)의 뉴스에서 급상승 키워드 후보를 상위 topN개 추출한다.
    /// baselineDays 만큼의 직전 기간을 기준선으로 삼아 spike(평소 대비 몇 배 등장) 를 계산한다.
    /// </summary>
    public async Task<List<KeywordCandidate>> ExtractCandidatesAsync(
        string databasePath, DateTimeOffset currentStart, DateTimeOffset currentEnd, int baselineDays, int topN, CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={databasePath}");
        await conn.OpenAsync(ct);

        var current = await LoadRowsAsync(conn, currentStart, currentEnd, cap: 8000, ct);
        var baselineStart = currentStart.AddDays(-baselineDays);
        var baseline = await LoadRowsAsync(conn, baselineStart, currentStart, cap: 20000, ct);

        _logger.LogInformation("[키워드] 대상 {cur}건 / 기준선 {base}건 로드 (윈도우 {s}~{e})",
            current.Count, baseline.Count, currentStart.ToString("MM-dd HH:mm"), currentEnd.ToString("MM-dd HH:mm"));

        // 기준선 term별 문서 빈도 (여러 기사에 등장한 횟수)
        var baselineFreq = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in baseline)
            foreach (var term in ExtractTermsFromRow(row).Distinct())
                baselineFreq[term] = baselineFreq.GetValueOrDefault(term) + 1;

        // 기준선 윈도우 개수 (현재 윈도우 길이 대비 몇 배 기간인지) — 시간당 평균으로 환산해 spike 계산
        var currentHours = Math.Max((currentEnd - currentStart).TotalHours, 0.5);
        var baselineHours = Math.Max((currentStart - baselineStart).TotalHours, currentHours);
        var scaleToWindow = currentHours / baselineHours; // 기준선 전체 → 현재 윈도우 길이로 환산

        // 현재 윈도우 집계
        var freq = new Dictionary<string, int>(StringComparer.Ordinal);
        var relatedIds = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var sampleTitles = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var catCount = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

        foreach (var row in current)
        {
            foreach (var term in ExtractTermsFromRow(row).Distinct())
            {
                freq[term] = freq.GetValueOrDefault(term) + 1;

                var ids = relatedIds.TryGetValue(term, out var l) ? l : (relatedIds[term] = new List<string>());
                if (ids.Count < 30) ids.Add(row.Id);

                var titles = sampleTitles.TryGetValue(term, out var t) ? t : (sampleTitles[term] = new List<string>());
                if (titles.Count < 8 && !titles.Contains(row.Title)) titles.Add(row.Title);

                var cc = catCount.TryGetValue(term, out var c) ? c : (catCount[term] = new Dictionary<string, int>(StringComparer.Ordinal));
                var cat = string.IsNullOrWhiteSpace(row.Category) ? "일반" : row.Category;
                cc[cat] = cc.GetValueOrDefault(cat) + 1;
            }
        }

        var candidates = new List<KeywordCandidate>();
        foreach (var (term, f) in freq)
        {
            if (f < 2) continue; // 최소 2건 이상 등장한 것만 (일회성 노이즈 제거)

            var expected = baselineFreq.GetValueOrDefault(term) * scaleToWindow;
            var spike = f / (expected + 0.8); // 라플라스 스무딩: 평소 거의 안 나오던 단어가 튀면 spike ↑
            // 점수: 빈도(로그) × 급상승. 완전 신규(기준선 0)는 spike가 커지되 과도하지 않게 캡.
            var score = Math.Log(f + 1) * Math.Min(spike, 12.0);

            var topCat = catCount[term].OrderByDescending(kv => kv.Value).First().Key;
            candidates.Add(new KeywordCandidate(
                term, f, Math.Round(spike, 2), Math.Round(score, 3), topCat,
                relatedIds[term], sampleTitles[term]));
        }

        var ranked = candidates.OrderByDescending(c => c.Score).Take(topN).ToList();
        _logger.LogInformation("[키워드] 통계 후보 {n}개 (전체 term {total}개 중)", ranked.Count, candidates.Count);
        return ranked;
    }

    private static async Task<List<NewsRow>> LoadRowsAsync(SqliteConnection conn, DateTimeOffset start, DateTimeOffset end, int cap, CancellationToken ct)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Title, COALESCE(Summary,''), COALESCE(Category,'일반')
            FROM RawNews
            WHERE PublishedAt >= $start AND PublishedAt < $end
            ORDER BY PublishedAt DESC
            LIMIT $cap;
            """;
        cmd.Parameters.AddWithValue("$start", start.ToString("O"));
        cmd.Parameters.AddWithValue("$end", end.ToString("O"));
        cmd.Parameters.AddWithValue("$cap", cap);

        var rows = new List<NewsRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new NewsRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return rows;
    }

    private static IEnumerable<string> ExtractTermsFromRow(NewsRow row)
        => Tokenize(row.Title + " " + row.Summary);

    // 제목+요약 텍스트 → 정규화된 명사형 토큰 목록
    internal static IEnumerable<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        foreach (Match m in TokenRegex.Matches(text))
        {
            var raw = m.Value.Trim();
            if (raw.Length < 2) continue;

            var norm = Normalize(raw);
            if (norm.Length < 2) continue;
            if (norm.All(char.IsDigit)) continue;                 // 숫자만
            if (StopWords.Contains(norm)) continue;
            if (norm.Length >= 2 && StopWords.Contains(norm.ToLowerInvariant())) continue;

            yield return norm;
        }
    }

    private static string Normalize(string token)
    {
        // 영문은 소문자로
        if (token.All(c => c < 128)) return token.ToLowerInvariant();

        // 한글: 뒤에 붙은 조사/어미를 긴 것부터 한 번 제거 (어근 2자 이상 유지될 때만)
        foreach (var josa in JosaSuffixes)
        {
            if (token.Length > josa.Length + 1 && token.EndsWith(josa, StringComparison.Ordinal))
                return token[..^josa.Length];
        }
        return token;
    }

    public sealed record RefinedKeyword(
        string Keyword, string Category, string Gist,
        double Score, int Frequency, double SpikeRatio,
        List<string> RelatedNewsIds, List<string> SampleTitles,
        double? SearchVolume = null);

    /// <summary>
    /// 통계 후보 + 대표 제목을 로컬 Gemma에 넣어, 같은 주제를 합치고 사람이 읽기 좋은 블로그 키워드로 정제한다.
    /// Gemma가 참조한 후보 번호(sources)로 원래 후보의 관련기사/빈도/급상승을 되짚어 합산한다.
    /// </summary>
    public async Task<List<RefinedKeyword>> RefineWithGemmaAsync(List<KeywordCandidate> candidates, CancellationToken ct)
    {
        if (candidates.Count == 0) return new List<RefinedKeyword>();

        // 컨텍스트(8192) 초과로 빈 응답이 나지 않도록 상위 45개만, 제목도 짧게 잘라서 넣는다.
        var forPrompt = candidates.Take(45).ToList();
        var lines = forPrompt.Select((c, i) =>
            $"{i + 1}. ({c.Category}) {c.Term} · 대표제목: {string.Join(" / ", c.SampleTitles.Take(2).Select(t => t.Length > 40 ? t[..40] : t))}");

        var prompt = $$"""
            당신은 뉴스 트렌드 분석가입니다. 아래는 최근 뉴스에서 통계로 추출한 "급상승 키워드 후보"와 대표 기사 제목입니다.
            이 후보들을 바탕으로 네이버 블로그 글감으로 좋은 핵심 키워드를 10~15개 선정하세요.

            [규칙]
            - 같은 사건/주제를 가리키는 후보들은 하나의 키워드로 합치세요.
            - 키워드는 사람이 실제로 검색할 법한 명사(구) 형태로 다듬으세요. (예: "차세대 KTX", "윤한홍 특검")
            - 조사가 붙은 조각이나 의미 없는 흔한 단어는 버리세요.
            - 각 키워드마다 category(정치/경제/사회/IT/연예/스포츠/일반 중 하나), gist(왜 지금 소재인지 한 줄), sources(사용한 후보 번호 배열)를 채우세요.

            반드시 아래 JSON 객체 형식으로만 응답하세요. 마크다운 백틱이나 설명 금지.
            {
              "keywords": [
                { "keyword": "...", "category": "...", "gist": "...", "sources": [1, 2] }
              ]
            }

            [후보 목록]
            {{string.Join("\n", lines)}}

            응답 JSON:
            """;

        var raw = await _gemma.CallGemmaJsonGpuAsync(prompt, ct);
        if (string.IsNullOrWhiteSpace(raw))
        {
            _logger.LogWarning("[키워드] Gemma 응답이 비어있어 정제 실패");
            return new List<RefinedKeyword>();
        }

        // 앞뒤 노이즈 제거 후 마지막 '}'까지만 파싱
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            _logger.LogWarning("[키워드] Gemma 응답에서 JSON을 찾지 못함: {raw}", raw.Length > 300 ? raw[..300] : raw);
            return new List<RefinedKeyword>();
        }

        var results = new List<RefinedKeyword>();
        try
        {
            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty("keywords", out var arr)) return results;

            foreach (var kw in arr.EnumerateArray())
            {
                var keyword = kw.TryGetProperty("keyword", out var k) ? k.GetString()?.Trim() ?? "" : "";
                if (string.IsNullOrWhiteSpace(keyword)) continue;

                var category = kw.TryGetProperty("category", out var c) ? c.GetString()?.Trim() ?? "일반" : "일반";
                var gist = kw.TryGetProperty("gist", out var g) ? g.GetString()?.Trim() ?? "" : "";

                var sourceIdx = new List<int>();
                if (kw.TryGetProperty("sources", out var s) && s.ValueKind == JsonValueKind.Array)
                    foreach (var el in s.EnumerateArray())
                        if (el.TryGetInt32(out var idx) && idx >= 1 && idx <= forPrompt.Count) sourceIdx.Add(idx - 1);

                var used = sourceIdx.Distinct().Select(i => forPrompt[i]).ToList();
                var relatedIds = used.SelectMany(u => u.RelatedNewsIds).Distinct().ToList();
                var titles = used.SelectMany(u => u.SampleTitles).Distinct().Take(8).ToList();
                var freq = used.Count > 0 ? used.Sum(u => u.Frequency) : 0;
                var score = used.Count > 0 ? used.Max(u => u.Score) : 0;
                var spike = used.Count > 0 ? used.Max(u => u.SpikeRatio) : 0;

                results.Add(new RefinedKeyword(keyword, category, gist, score, freq, spike, relatedIds, titles));
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("[키워드] Gemma JSON 파싱 실패: {msg}", ex.Message);
            return new List<RefinedKeyword>();
        }

        _logger.LogInformation("[키워드] Gemma 정제 완료 — {n}개 키워드", results.Count);
        return results;
    }

    /// <summary>통계 후보 추출 → Gemma 정제 → NewsKeywords 저장까지 한 번에 수행. 저장된 키워드 수를 반환.</summary>
    public async Task<int> ExtractAndSaveAsync(
        string databasePath, DateTimeOffset currentStart, DateTimeOffset currentEnd, int baselineDays, int topCandidates, CancellationToken ct)
    {
        var candidates = await ExtractCandidatesAsync(databasePath, currentStart, currentEnd, baselineDays, topCandidates, ct);
        if (candidates.Count == 0)
        {
            _logger.LogInformation("[키워드] 대상 기간에 후보가 없어 건너뜀");
            return 0;
        }

        var refined = await RefineWithGemmaAsync(candidates, ct);
        if (refined.Count > 0)
        {
            // Gemma의 소스 번호는 부정확할 수 있으므로, 최종 키워드를 실제 뉴스 텍스트와 재매칭해 관련 기사를 정확히 붙인다.
            refined = await GroundRelatedArticlesAsync(databasePath, refined, currentStart, currentEnd, ct);

            // C단계: 네이버 DataLab 검색량 크로스체크 (권한/네트워크 실패 시 값 없는 채로 진행 — B는 그대로 유효)
            var volumes = await _dataLab.GetSearchVolumesAsync(refined.Select(k => k.Keyword).ToList(), ct);
            if (volumes.Count > 0)
                refined = refined.Select(k => volumes.TryGetValue(k.Keyword, out var v) ? k with { SearchVolume = v } : k).ToList();

            await SaveAsync(databasePath, refined, "gemma-hybrid", currentStart, currentEnd, ct);
            return refined.Count;
        }

        // 폴백: Gemma가 실패하면 통계 상위 후보라도 저장해 결과가 비지 않게 한다.
        _logger.LogWarning("[키워드] Gemma 정제 실패 → 통계 상위 후보 20개로 폴백 저장");
        var fallback = candidates.Take(20).Select(c => new RefinedKeyword(
            c.Term, c.Category, "(통계 자동) 급상승 키워드", c.Score, c.Frequency, c.SpikeRatio,
            c.RelatedNewsIds, c.SampleTitles)).ToList();
        await SaveAsync(databasePath, fallback, "statistical-only", currentStart, currentEnd, ct);
        return fallback.Count;
    }

    /// <summary>
    /// 최종 키워드를 대상 기간 RawNews의 제목+요약과 텍스트 매칭해서 관련 기사(Id/제목)와 빈도를 정확히 다시 채운다.
    /// 키워드의 핵심 단어를 모두 포함하는 기사를 우선(AND), 2건 미만이면 일부라도 포함(OR)한 기사를 매칭 수 순으로.
    /// </summary>
    private async Task<List<RefinedKeyword>> GroundRelatedArticlesAsync(
        string databasePath, List<RefinedKeyword> keywords, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={databasePath}");
        await conn.OpenAsync(ct);
        var rows = await LoadRowsAsync(conn, start, end, cap: 8000, ct);

        var result = new List<RefinedKeyword>();
        foreach (var kw in keywords)
        {
            var words = Tokenize(kw.Keyword).Distinct().ToList();
            if (words.Count == 0) { result.Add(kw); continue; }

            var scored = rows
                .Select(r =>
                {
                    var text = r.Title + " " + r.Summary;
                    var m = words.Count(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
                    return (Row: r, Match: m);
                })
                .Where(x => x.Match > 0)
                .ToList();

            var allMatch = scored.Where(x => x.Match == words.Count).ToList();
            var chosen = (allMatch.Count >= 2 ? allMatch : scored)
                .OrderByDescending(x => x.Match)
                .Take(15)
                .ToList();

            if (chosen.Count == 0) { result.Add(kw); continue; } // 매칭 실패 시 원본 유지

            var ids = chosen.Select(x => x.Row.Id).ToList();
            var titles = chosen.Select(x => x.Row.Title).Distinct().Take(8).ToList();
            result.Add(kw with { RelatedNewsIds = ids, SampleTitles = titles, Frequency = chosen.Count });
        }

        _logger.LogInformation("[키워드] 관련 기사 재매칭 완료 ({n}개 키워드)", result.Count);
        return result;
    }

    private async Task SaveAsync(string databasePath, List<RefinedKeyword> keywords, string method, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={databasePath}");
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // 하루 단위 갱신: 같은 날(WindowStart 날짜)의 기존 키워드를 지우고 새로 넣어, 재실행해도 중복이 안 쌓이게 한다.
        var del = conn.CreateCommand();
        del.Transaction = (SqliteTransaction)tx;
        del.CommandText = "DELETE FROM NewsKeywords WHERE date(WindowStart) = date($ws);";
        del.Parameters.AddWithValue("$ws", windowStart.ToString("O"));
        var removed = await del.ExecuteNonQueryAsync(ct);
        if (removed > 0) _logger.LogInformation("[키워드] 같은 날 기존 키워드 {n}건 갱신(교체)", removed);

        foreach (var kw in keywords)
        {
            var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO NewsKeywords (Keyword, Category, Gist, Score, Frequency, SpikeRatio, SearchVolume, RelatedNewsIds, SampleTitles, Method, WindowStart, WindowEnd)
                VALUES ($kw, $cat, $gist, $score, $freq, $spike, $vol, $ids, $titles, $method, $ws, $we);
                """;
            cmd.Parameters.AddWithValue("$kw", kw.Keyword);
            cmd.Parameters.AddWithValue("$cat", kw.Category);
            cmd.Parameters.AddWithValue("$gist", (object?)kw.Gist ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$score", kw.Score);
            cmd.Parameters.AddWithValue("$freq", kw.Frequency);
            cmd.Parameters.AddWithValue("$spike", kw.SpikeRatio);
            cmd.Parameters.AddWithValue("$vol", (object?)kw.SearchVolume ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(kw.RelatedNewsIds));
            cmd.Parameters.AddWithValue("$titles", JsonSerializer.Serialize(kw.SampleTitles));
            cmd.Parameters.AddWithValue("$method", method);
            cmd.Parameters.AddWithValue("$ws", windowStart.ToString("O"));
            cmd.Parameters.AddWithValue("$we", windowEnd.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        _logger.LogInformation("[키워드] NewsKeywords에 {n}개 저장 완료", keywords.Count);
    }
}
