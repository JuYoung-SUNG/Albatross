using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Albatross.Collector.News.Services;

/// <summary>
/// 네이버 블로그 검색 API(openapi.naver.com/v1/search/blog.json).
///
/// 정렬은 반드시 date를 쓴다. sim은 결과가 엉망이다 —
/// "좌타석"으로 검색하면 야구 유망주 글이 나오고, total도 크게 부풀려진다
/// (위례신도시골프연습장: sim 10,489 vs date 748).
///
/// 여기서 얻는 것:
///  1) total — 그 키워드로 잡히는 글 수. <b>경쟁 지표로는 못 쓴다</b>(아래 참고).
///  2) 상위 글 목록 — 지금 그 키워드를 누가 먹고 있는지.
///  3) ExactTitleMatches — 상위 글 중 <b>제목에 키워드가 그대로 들어간</b> 글 수. 이게 진짜 경쟁 지표다.
///
/// total을 믿으면 안 되는 이유 — 네이버는 검색어를 쪼개 느슨하게 매칭한다.
/// "골프연습장 좌타석"의 total이 16,822인데 상위 10개 중 그 표현을 실제로 담은 글은 0개였다.
/// 즉 아무도 안 쓴 주제인데 경쟁이 심한 것처럼 보인다. 큰따옴표 구문 검색도 통하지 않는다
/// (10,489 → 10,477로 사실상 동일). 그래서 상위 결과를 직접 세는 방식을 쓴다.
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

    /// <param name="TotalCount">네이버가 보고하는 전체 건수. 느슨한 매칭이라 참고용.</param>
    /// <param name="Analyzed">정확도 계산에 쓴 상위 글 수(보통 10).</param>
    /// <param name="ExactTitleMatches">그중 제목에 키워드가 그대로 들어간 글 수 — 실질 경쟁.</param>
    /// <param name="ExactAnyMatches">제목 또는 요약에 키워드가 그대로 들어간 글 수.</param>
    public sealed record BlogSearchResult(
        string Keyword,
        int TotalCount,
        int Analyzed,
        int ExactTitleMatches,
        int ExactAnyMatches,
        string? LatestPostDate,
        List<BlogPost> TopPosts);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NAVER_CLIENT_ID")) &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NAVER_CLIENT_SECRET"));

    /// <summary>
    /// 정확도순 상위 글을 가져와 경쟁 지표를 계산한다.
    /// analyzeCount 만큼 받아서 정확 일치를 세고, 그중 keepPosts 개만 결과에 담는다.
    /// </summary>
    public async Task<BlogSearchResult?> SearchAsync(
        string keyword, int keepPosts, CancellationToken ct, int analyzeCount = 10)
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
            var display = Math.Clamp(Math.Max(analyzeCount, keepPosts), 1, 100);
            var url = $"{Endpoint}?query={Uri.EscapeDataString(keyword)}&display={display}&start=1&sort=date";
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
            int titleHits = 0, anyHits = 0, analyzed = 0;

            // 공백을 지운 상태로 비교한다. "골프연습장 가격"과 "골프연습장가격"을 같은 것으로 보기 위해서다.
            var needle = Squeeze(keyword);

            if (root.TryGetProperty("items", out var items))
            {
                foreach (var it in items.EnumerateArray())
                {
                    var title = Clean(Str(it, "title"));
                    var snippet = Clean(Str(it, "description"));

                    analyzed++;
                    var inTitle = Squeeze(title).Contains(needle, StringComparison.OrdinalIgnoreCase);
                    if (inTitle) titleHits++;
                    if (inTitle || Squeeze(snippet).Contains(needle, StringComparison.OrdinalIgnoreCase)) anyHits++;

                    if (posts.Count < keepPosts)
                        posts.Add(new BlogPost(title, Str(it, "link"), Clean(Str(it, "bloggername")),
                            FormatDate(Str(it, "postdate")), snippet));
                }
            }

            return new BlogSearchResult(keyword, total, analyzed, titleHits, anyHits,
                posts.Count > 0 ? posts.Max(p => p.PostDate) : null, posts);
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

    private static string Squeeze(string s) => Regex.Replace(s ?? "", @"\s+", "");

    /// <summary>"20250315" → "2025-03-15"</summary>
    private static string FormatDate(string yyyymmdd) =>
        yyyymmdd.Length == 8
            ? $"{yyyymmdd[..4]}-{yyyymmdd.Substring(4, 2)}-{yyyymmdd.Substring(6, 2)}"
            : yyyymmdd;
}
