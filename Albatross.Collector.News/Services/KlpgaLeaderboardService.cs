using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Albatross.Collector.News.Services;

/// <summary>
/// 대회별 최종 순위(리더보드)를 가져온다.
///
///   POST /load/leaderboard/roundLeaderboard   gameCode=...&amp;round=N
///
/// round 값이 핵심이다. 빼고 부르면 1라운드 순위가 오고, 대회의 마지막 라운드 번호를 줘야
/// 최종 순위가 온다. 실제로 round 없이 부른 응답의 1위와 우승자가 서로 달랐다.
///
/// 응답은 HTML이지만 각 선수 행이 data-* 속성으로 값을 다 들고 있어 파싱이 안정적이다.
///   &lt;li id="favoritItem_8392" data-rank="1" data-name="이다연" data-totunderpar="-12"
///       data-score="69" data-round1score="73" ... &gt;
/// </summary>
public class KlpgaLeaderboardService
{
    private const string Url = "https://klpga.co.kr/load/leaderboard/roundLeaderboard";

    private readonly HttpClient _http;
    private readonly ILogger<KlpgaLeaderboardService> _logger;

    public KlpgaLeaderboardService(HttpClient http, ILogger<KlpgaLeaderboardService> logger)
    {
        _http = http;
        _logger = logger;
        // 응답이 대회당 1~2MB라 기본 타임아웃으로는 부족할 수 있다
        _http.Timeout = TimeSpan.FromMinutes(2);
    }

    public sealed record LeaderboardEntry(
        int Rank,
        string PlayerCode,
        string PlayerName,
        string? TotalUnderPar,
        int? TotalStrokes,
        int? Round1, int? Round2, int? Round3, int? Round4);

    private static readonly Regex RowRegex = new(
        """
        <li id="favoritItem_(\d+)"[^>]*?data-rank="([^"]*)"[^>]*?data-name="([^"]*)"[^>]*?data-totunderpar="([^"]*)"[^>]*?data-inghole="[^"]*"[^>]*?data-todayunderpar="[^"]*"[^>]*?data-score="([^"]*)"[^>]*?data-round1score="([^"]*)"[^>]*?data-round2score="([^"]*)"[^>]*?data-round3score="([^"]*)"[^>]*?data-round4score="([^"]*)"
        """.Trim(), RegexOptions.Compiled);

    /// <summary>
    /// 대회 하나의 최종 순위. rounds는 그 대회의 라운드 수(보통 3 또는 4).
    /// 컷 탈락자는 응답에 포함되지 않으므로 결과는 컷 통과 인원만큼 나온다.
    /// </summary>
    public async Task<List<LeaderboardEntry>> GetFinalLeaderboardAsync(
        string gameCode, int rounds, CancellationToken ct)
    {
        var result = new List<LeaderboardEntry>();
        var round = rounds is >= 1 and <= 4 ? rounds : 4;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Url)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["gameCode"] = gameCode,
                    ["round"] = round.ToString()
                })
            };
            req.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");
            req.Headers.Add("Referer", "https://klpga.co.kr/web/leaderboard/leaderboard");
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");

            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("[리더보드] {code} 조회 실패 {status}", gameCode, (int)res.StatusCode);
                return result;
            }

            var html = await res.Content.ReadAsStringAsync(ct);

            // 같은 선수가 여러 번 나올 수 있어(레이아웃 반복) 코드 기준으로 한 번만 담는다
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in RowRegex.Matches(html))
            {
                var code = m.Groups[1].Value;
                if (!seen.Add(code)) continue;
                if (!int.TryParse(m.Groups[2].Value, out var rank)) continue;   // 컷 탈락·기권은 순위가 비어 있다

                result.Add(new LeaderboardEntry(
                    rank,
                    code,
                    WebUtility.HtmlDecode(m.Groups[3].Value).Trim(),
                    Blank(m.Groups[4].Value),
                    Num(m.Groups[5].Value),
                    Num(m.Groups[6].Value), Num(m.Groups[7].Value),
                    Num(m.Groups[8].Value), Num(m.Groups[9].Value)));
            }

            result = result.OrderBy(e => e.Rank).ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("[리더보드] {code} 처리 중 오류: {msg}", gameCode, ex.Message);
        }
        return result;
    }

    private static int? Num(string s) => int.TryParse(s?.Trim(), out var v) && v > 0 ? v : null;
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
