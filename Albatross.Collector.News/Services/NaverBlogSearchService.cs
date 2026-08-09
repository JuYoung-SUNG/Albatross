using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Albatross.Collector.News.Services;

/// <summary>
/// 네이버 블로그 검색 API(openapi.naver.com/v1/search/blog.json).
///
/// 여기서 얻는 것은 두 가지다.
///  1) total — 그 키워드로 쓰인 블로그 글 수. 사실상 "경쟁 강도"다.
///  2) 상위 글 목록 — 지금 그 키워드를 누가 먹고 있는지.
///
/// 조회수·공감수·댓글수는 이 API로 나오지 않는다(네이버가 주지 않는다).
/// 뉴스 수집과 같은 자격증명(NAVER_CLIENT_ID/SECRET)을 쓰며, 별도 신청 없이 기본 제공된다.
/// </summary>
public class NaverBlogSearchService
{
    private const string Endpoint = "https://openapi.naver.com/v1/search/blog.json";

    private readonly HttpClient _http;
    private readonly ILogger<NaverBlogSearchService> _logger;

    public NaverBlogSearchService(HttpClient http, ILogger<NaverBlogSearchService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public sealed record BlogPost(string Title, string Link, string BloggerName, string PostDate, string Snippet);

    /// <summary>키워드 하나의 검색 결과. TotalCount가 그 키워드의 누적 문서 수다.</summary>
    public sealed record BlogSearchResult(string Keyword, int TotalCount, List<BlogPost> TopPosts);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NAVER_CLIENT_ID")) &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NAVER_CLIENT_SECRET"));

    /// <summary>정확도순(sim) 상위 topN개와 전체 문서 수를 가져온다.</summary>
    public async Task<BlogSearchResult?> SearchAsync(string keyword, int topN, CancellationToken ct)
    {
        var clientId = Environment.GetEnvironmentVariable("NAVER_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("NAVER_CLIENT_SECRET");
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            _logger.LogWarning("[블로그] NAVER_CLIENT_ID/SECRET 미설정 — 블로그 검색을 건너뜁니다");
            return null;
        }

        try
        {
            var url = $"{Endpoint}?query={Uri.EscapeDataString(keyword)}&display={Math.Clamp(topN, 1, 100)}&start=1&sort=sim";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Naver-Client-Id", clientId);
            req.Headers.Add("X-Naver-Client-Secret", clientSecret);

            using var res = await _http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("[블로그] '{kw}' 검색 실패 {code}", keyword, (int)res.StatusCode);
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var total = root.TryGetProperty("total", out var t) && t.TryGetInt32(out var tv) ? tv : 0;

            var posts = new List<BlogPost>();
            if (root.TryGetProperty("items", out var items))
            {
                foreach (var it in items.EnumerateArray())
                {
                    posts.Add(new BlogPost(
                        Clean(Str(it, "title")),
                        Str(it, "link"),
                        Clean(Str(it, "bloggername")),
                        FormatDate(Str(it, "postdate")),
                        Clean(Str(it, "description"))));
                }
            }

            return new BlogSearchResult(keyword, total, posts);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("[블로그] '{kw}' 검색 중 오류: {msg}", keyword, ex.Message);
            return null;
        }
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";

    /// <summary>검색어에 &lt;b&gt; 태그가 붙어 오고 HTML 엔티티도 섞여 있어 걷어낸다.</summary>
    private static string Clean(string s) =>
        WebUtility.HtmlDecode(Regex.Replace(s ?? "", "<.*?>", "")).Trim();

    /// <summary>"20250315" → "2025-03-15"</summary>
    private static string FormatDate(string yyyymmdd) =>
        yyyymmdd.Length == 8
            ? $"{yyyymmdd[..4]}-{yyyymmdd.Substring(4, 2)}-{yyyymmdd.Substring(6, 2)}"
            : yyyymmdd;
}
