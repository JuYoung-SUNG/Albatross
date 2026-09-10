using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Albatross.Collector.News.Services;

/// <summary>
/// 네이버 자동완성(ac.search.naver.com)에서 연관 키워드를 가져온다.
///
/// 왜 이게 필요한가 — 검색광고 API의 연관키워드(relKeyword)가 가장 좋지만 API 키가 있어야 한다.
/// 자동완성은 키가 필요 없고, 실제로 사람들이 많이 치는 조합이 위로 올라오므로
/// "글감 후보"를 넓히는 용도로는 충분히 쓸 만하다.
///
/// 검색량은 주지 않는다. 그건 검색광고 API의 몫이다.
/// </summary>
public class NaverAutocompleteService
{
    private const string Endpoint = "https://ac.search.naver.com/nx/ac";

    private readonly HttpClient _http;
    private readonly ILogger<NaverAutocompleteService> _logger;

    public NaverAutocompleteService(HttpClient http, ILogger<NaverAutocompleteService> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>시드 하나의 자동완성 제안어. 보통 10개 안팎이 온다.</summary>
    public async Task<List<string>> SuggestAsync(string seed, CancellationToken ct)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(seed)) return result;

        try
        {
            var url = $"{Endpoint}?q={Uri.EscapeDataString(seed)}&con=0&frm=nv&ans=2" +
                      "&r_format=json&r_enc=UTF-8&r_unicode=0&t_koreng=1&run=2&rev=4&q_enc=UTF-8&st=100";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");
            req.Headers.Add("Referer", "https://search.naver.com/");

            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return result;

            var body = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);

            // items 는 [[ ["키워드","0"], ["키워드2","0"] ]] 형태의 이중 배열이다
            if (!doc.RootElement.TryGetProperty("items", out var items)) return result;
            foreach (var group in items.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Array) continue;
                foreach (var pair in group.EnumerateArray())
                {
                    if (pair.ValueKind != JsonValueKind.Array) continue;
                    var first = pair.EnumerateArray().FirstOrDefault();
                    var word = first.ValueKind == JsonValueKind.String ? first.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(word)) result.Add(word!.Trim());
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("[자동완성] '{seed}' 조회 실패: {msg}", seed, ex.Message);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 시드에서 한 단계 더 넓힌다. 시드의 제안어들을 다시 자동완성에 넣어 후보를 늘린다.
    /// depth 2면 호출이 10배가 되므로 상한(maxKeywords)으로 끊는다.
    /// </summary>
    public async Task<List<string>> ExpandAsync(string seed, int depth, int maxKeywords, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { seed };
        var frontier = new List<string> { seed };

        for (var level = 0; level < Math.Max(depth, 1); level++)
        {
            var next = new List<string>();
            foreach (var word in frontier)
            {
                ct.ThrowIfCancellationRequested();
                if (seen.Count >= maxKeywords) break;

                foreach (var s in await SuggestAsync(word, ct))
                    if (seen.Add(s)) next.Add(s);

                await Task.Delay(200, ct);   // 공개 엔드포인트라 간격을 둔다
            }
            if (next.Count == 0) break;
            frontier = next;
        }

        var list = seen.Take(maxKeywords).ToList();
        _logger.LogInformation("[자동완성] '{seed}' → 후보 {n}개", seed, list.Count);
        return list;
    }
}
