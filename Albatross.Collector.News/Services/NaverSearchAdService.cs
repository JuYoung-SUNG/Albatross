using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Albatross.Collector.News.Services;

/// <summary>
/// 네이버 검색광고 API의 "키워드도구"(/keywordstool)로 월간 검색수와 경쟁정도를 가져온다.
///
/// 왜 이걸 쓰나 — 남의 블로그 "조회수"는 어떤 공개 API로도 못 가져온다(글쓴이만 보는 값).
/// 대신 "이 주제를 한 달에 몇 명이 검색하는가"는 실제 숫자로 알 수 있고,
/// 글감을 고르는 목적에는 조회수보다 이쪽이 더 정확한 지표다.
///
/// 자격증명(환경변수) — 검색 API(NAVER_CLIENT_ID/SECRET)와는 완전히 별개다.
///   NAVER_AD_API_KEY      네이버 검색광고 &gt; 도구 &gt; API 관리의 액세스라이선스
///   NAVER_AD_SECRET_KEY   같은 화면의 비밀키
///   NAVER_AD_CUSTOMER_ID  같은 화면의 CUSTOMER_ID (숫자)
/// 광고를 집행하지 않아도 계정만 있으면 무료로 쓸 수 있다.
/// </summary>
public class NaverSearchAdService
{
    private const string BaseUrl = "https://api.searchad.naver.com";
    private const string Path = "/keywordstool";

    private readonly HttpClient _http;
    private readonly ILogger<NaverSearchAdService> _logger;

    public NaverSearchAdService(HttpClient http, ILogger<NaverSearchAdService> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>키워드 하나의 검색 수요. 검색수가 10 미만이면 네이버가 "&lt; 10"을 주는데 5로 환산해 둔다.</summary>
    public sealed record KeywordVolume(
        string Keyword,
        int MonthlyPc,
        int MonthlyMobile,
        string Competition)
    {
        public int MonthlyTotal => MonthlyPc + MonthlyMobile;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NAVER_AD_API_KEY")) &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NAVER_AD_SECRET_KEY")) &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NAVER_AD_CUSTOMER_ID"));

    /// <summary>
    /// 키워드 목록의 월간 검색수를 조회한다.
    /// 키워드도구는 한 번에 힌트 키워드 5개까지 받으므로 5개씩 끊어 호출한다.
    /// 응답에는 연관 키워드까지 딸려 오는데, 요청한 키워드 외에도 쓸 만한 소재가 섞여 있어 함께 돌려준다.
    /// </summary>
    public async Task<Dictionary<string, KeywordVolume>> GetVolumesAsync(
        IEnumerable<string> keywords, bool includeRelated, CancellationToken ct)
    {
        var result = new Dictionary<string, KeywordVolume>(StringComparer.OrdinalIgnoreCase);

        var apiKey = Environment.GetEnvironmentVariable("NAVER_AD_API_KEY");
        var secretKey = Environment.GetEnvironmentVariable("NAVER_AD_SECRET_KEY");
        var customerId = Environment.GetEnvironmentVariable("NAVER_AD_CUSTOMER_ID");
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(secretKey) || string.IsNullOrWhiteSpace(customerId))
        {
            _logger.LogWarning("[검색량] NAVER_AD_* 환경변수 미설정 — 검색수 조회를 건너뜁니다");
            return result;
        }

        // 키워드도구는 공백이 들어가면 인식률이 떨어진다. 공백 제거 후 조회하고, 원래 표기로 되돌린다.
        var pairs = keywords
            .Select(k => (Original: k, Query: k.Replace(" ", "")))
            .Where(p => p.Query.Length > 0)
            .GroupBy(p => p.Query, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        foreach (var chunk in pairs.Chunk(5))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var hint = string.Join(",", chunk.Select(p => p.Query));
                var query = $"?hintKeywords={Uri.EscapeDataString(hint)}&showDetail=1";

                using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + Path + query);
                Sign(req, "GET", Path, apiKey!, secretKey!, customerId!);

                using var res = await _http.SendAsync(req, ct);
                var body = await res.Content.ReadAsStringAsync(ct);
                if (!res.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[검색량] 조회 실패 {code} — {body}", (int)res.StatusCode, Truncate(body, 200));
                    continue;
                }

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("keywordList", out var list)) continue;

                var requested = chunk.ToDictionary(p => p.Query, p => p.Original, StringComparer.OrdinalIgnoreCase);
                foreach (var item in list.EnumerateArray())
                {
                    var rel = item.TryGetProperty("relKeyword", out var rk) ? rk.GetString() ?? "" : "";
                    if (rel.Length == 0) continue;

                    // 요청한 키워드가 아니면 연관 키워드다. 원하지 않으면 건너뛴다.
                    var isRequested = requested.TryGetValue(rel, out var original);
                    if (!isRequested && !includeRelated) continue;

                    var vol = new KeywordVolume(
                        isRequested ? original! : rel,
                        ReadCount(item, "monthlyPcQcCnt"),
                        ReadCount(item, "monthlyMobileQcCnt"),
                        item.TryGetProperty("compIdx", out var ci) ? ci.GetString() ?? "미상" : "미상");

                    result[vol.Keyword] = vol;
                }

                // 검색광고 API는 초당 호출 제한이 있다. 짧게 쉬어 429를 피한다.
                await Task.Delay(300, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning("[검색량] 조회 중 오류: {msg}", ex.Message);
            }
        }

        _logger.LogInformation("[검색량] {n}개 키워드 검색수 확보", result.Count);
        return result;
    }

    /// <summary>
    /// 검색광고 API 인증 서명.
    /// 서명 대상은 "타임스탬프.메서드.경로" 이며 쿼리스트링은 제외한다.
    /// </summary>
    private static void Sign(HttpRequestMessage req, string method, string path, string apiKey, string secretKey, string customerId)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var message = $"{timestamp}.{method}.{path}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey));
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(message)));

        req.Headers.Add("X-Timestamp", timestamp);
        req.Headers.Add("X-API-KEY", apiKey);
        req.Headers.Add("X-Customer", customerId);
        req.Headers.Add("X-Signature", signature);
    }

    /// <summary>검색수는 숫자로 오지만, 10 미만이면 "&lt; 10" 문자열로 온다.</summary>
    private static int ReadCount(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var el)) return 0;
        if (el.ValueKind == JsonValueKind.Number) return el.TryGetInt32(out var n) ? n : 0;
        var s = el.GetString() ?? "";
        if (s.Contains('<')) return 5;                       // "< 10" → 중간값으로 5 취급
        return int.TryParse(s.Replace(",", ""), out var v) ? v : 0;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
