using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Albatross.Collector.News.Services;

/// <summary>
/// KLPGA 기록실(/web/record/publicRecord)에서 부문별 랭킹을 가져온다.
///
/// 이 페이지는 대회 목록과 달리 JSON 엔드포인트가 따로 없고, 서버가 HTML에 그려서 내려준다.
/// 다행히 구조가 규칙적이다 — 부문마다 &lt;h2 class="underline-title"&gt;제목&lt;/h2&gt; 이 있고
/// 그 아래 선수 링크(playerCode)와 기록값이 이어진다. 그래서 제목 위치로 구간을 잘라 파싱한다.
///
/// 상금순위·대상포인트부터 벙커세이브율·히팅능력지수까지 35개 부문이 한 페이지에 들어 있다.
/// </summary>
public class KlpgaRecordService
{
    private const string Url = "https://klpga.co.kr/web/record/publicRecord";

    private readonly HttpClient _http;
    private readonly ILogger<KlpgaRecordService> _logger;

    public KlpgaRecordService(HttpClient http, ILogger<KlpgaRecordService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public sealed record RankEntry(int Rank, string PlayerCode, string PlayerName, string? Team, string Value);

    /// <summary>부문 하나. Category가 곧 화면에 쓰는 부문명이다.</summary>
    public sealed record RankingCategory(string Category, List<RankEntry> Entries);

    // 선수 링크 → 이름 → (소속) → ... → 기록값 순서로 나온다.
    private static readonly Regex HeadingRegex =
        new("""<h2 class="underline-title[^"]*">([\s\S]*?)</h2>""", RegexOptions.Compiled);

    private static readonly Regex EntryRegex = new(
        """playerCode=(\d+)">\s*([^<]{1,20}?)\s*</a>\s*(?:<small>([^<]*)</small>)?[\s\S]{0,600}?<div class="col-\d+[^"]*">\s*([0-9,.\-]+)\s*</div>""",
        RegexOptions.Compiled);

    private static readonly Regex TagRegex = new("<[^>]*>", RegexOptions.Compiled);

    /// <summary>지정 시즌의 전체 부문 랭킹. 부문마다 상위 topN명만 담는다.</summary>
    public async Task<List<RankingCategory>> GetRankingsAsync(int season, int topN, CancellationToken ct)
    {
        var result = new List<RankingCategory>();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Url)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["season"] = season.ToString()
                })
            };
            req.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");
            req.Headers.Add("Referer", "https://klpga.co.kr/web/main/index");

            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("[기록] {season} 랭킹 조회 실패 {code}", season, (int)res.StatusCode);
                return result;
            }

            var html = await res.Content.ReadAsStringAsync(ct);
            var heads = HeadingRegex.Matches(html)
                .Select(m => (Name: Clean(m.Groups[1].Value), At: m.Index))
                .Where(h => h.Name.Length > 0)
                .ToList();

            if (heads.Count == 0)
            {
                _logger.LogWarning("[기록] 부문 제목을 찾지 못했습니다 — 협회 사이트 구조가 바뀌었을 수 있습니다");
                return result;
            }

            for (var i = 0; i < heads.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var start = heads[i].At;
                var end = i + 1 < heads.Count ? heads[i + 1].At : html.Length;
                var segment = html[start..end];

                var entries = new List<RankEntry>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (Match m in EntryRegex.Matches(segment))
                {
                    var code = m.Groups[1].Value;
                    if (!seen.Add(code)) continue;          // 같은 선수가 사진/이름 두 번 걸리는 경우 제거
                    entries.Add(new RankEntry(
                        entries.Count + 1,
                        code,
                        Clean(m.Groups[2].Value),
                        Blank(Clean(m.Groups[3].Value)),
                        Clean(m.Groups[4].Value)));
                    if (entries.Count >= topN) break;
                }

                if (entries.Count > 0)
                    result.Add(new RankingCategory(heads[i].Name, entries));
            }

            _logger.LogInformation("[기록] {season} 시즌 {n}개 부문 수집 (선수 연인원 {p}명)",
                season, result.Count, result.Sum(r => r.Entries.Count));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("[기록] {season} 조회 중 오류: {msg}", season, ex.Message);
        }
        return result;
    }

    private static string Clean(string s) =>
        WebUtility.HtmlDecode(TagRegex.Replace(s ?? "", "")).Replace("&nbsp;", " ").Trim();

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
