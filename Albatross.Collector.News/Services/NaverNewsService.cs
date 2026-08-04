using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Albatross.Collector.News.Models;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using HtmlAgilityPack;
using System.Net;
using Microsoft.Data.Sqlite;

namespace Albatross.Collector.News.Services;

public class NaverNewsService : INewsService
{
    private readonly HttpClient _http;
    private readonly ILogger<NaverNewsService> _logger;
    private readonly IConfiguration _config;

    public NaverNewsService(HttpClient http, ILogger<NaverNewsService> logger, IConfiguration config)
    {
        _http = http;
        _logger = logger;
        _config = config;
        
        // 브라우저처럼 보이게 하여 크롤링 차단 방지
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }
    }

    public async Task<IEnumerable<NewsItem>> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        var clientId = Environment.GetEnvironmentVariable("NAVER_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("NAVER_CLIENT_SECRET");

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            _logger.LogWarning("NAVER_CLIENT_ID or NAVER_CLIENT_SECRET environment variables are not set.");
            return Enumerable.Empty<NewsItem>();
        }

        // 설정된 키워드로 요청 (최근 뉴스를 최대한 많이 수집하기 위해 최대치인 100건으로 요청)
        var query = _config["Collector:SearchKeyword"] ?? "KBO";
        var url = $"https://openapi.naver.com/v1/search/news.json?query={Uri.EscapeDataString(query)}&display=100&sort=date";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Naver-Client-Id", clientId);
        request.Headers.Add("X-Naver-Client-Secret", clientSecret);

        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Naver API error: {StatusCode}, Body: {Body}", response.StatusCode, errorBody);
            return Enumerable.Empty<NewsItem>();
        }

        var jsonString = await response.Content.ReadAsStringAsync(cancellationToken);
        //_logger.LogInformation("Naver API Full Response: {Json}", jsonString);

        var result = JsonSerializer.Deserialize<NaverNewsResponse>(jsonString, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (result == null || result.Items == null)
            return Enumerable.Empty<NewsItem>();

        // ⏰ [시간 필터 설정] 현재 시간 기준 설정된 분(기본 10분) 전 타임스탬프 계산
        var recencyMinutes = _config.GetValue<int>("Collector:RecencyMinutes", 10);
        var timeCutoff = DateTimeOffset.Now.AddMinutes(-recencyMinutes);
        _logger.LogInformation("⏳ Naver 뉴스 시간 필터링 활성화: {time} 이후에 발행된 뉴스만 골라냅니다.", timeCutoff.ToString("yyyy-MM-dd HH:mm:ss"));

        var items = new List<NewsItem>();
        foreach (var item in result.Items)
        {
            var pubDate = ParsePubDate(item.PubDate);
            
            // 🎯 [핵심 필터 적용] 10분 이내 뉴스만 골라내기
            if (pubDate < timeCutoff)
            {
                continue;
            }

            var content = string.Empty;
            var imageUrls = new List<string>();

            // [임시 비활성화] n.news.naver.com 기사 페이지를 직접 열어 본문/이미지를 긁어오는 로직은 현재 사용하지 않는다.
            // (한 시간마다 최대한 많은 기사 "목록"만 빠르게 수집하는 것이 목적이라 개별 본문 크롤링은 생략)
            // if (item.Link.Contains("n.news.naver.com"))
            // {
            //     var (scrapedContent, scrapedImages) = await ScrapeNaverNewsAsync(item.Link, cancellationToken);
            //     content = scrapedContent;
            //     imageUrls = scrapedImages;
            // }

            items.Add(new NewsItem(
                id: Guid.NewGuid().ToString(),
                title: CleanHtml(item.Title),
                summary: CleanHtml(item.Description),
                url: item.Link,
                publishedAt: pubDate
            )
            {
                Source = "네이버 뉴스",
                Category = "기타",
                Country = "한국",
                Content = string.IsNullOrEmpty(content) ? null : content,
                ImageUrl = imageUrls.Count > 0 ? string.Join("/", imageUrls) : null
            });
        }

        _logger.LogInformation("Naver news filtering completed. Original: {original}, Filtered: {filtered}", result.Items.Count(), items.Count);
        return items;
    }

    /// <summary>
    /// 네이버 스포츠의 KBO리그 뉴스 "날짜별 목록" 공개 API로 특정 발행일의 뉴스를 전부 가져온다.
    /// (https://sports.news.naver.com/kbaseball/news/list?isphoto=N&date=YYYYMMDD&page=N — 오픈API 키 불필요)
    /// 실시간 수집(GetLatestAsync)은 최근 10분치만 잡는 것과 달리, 이 메서드는 과거 어떤 날짜든 소급 조회할 수 있다.
    /// 하루 약 15페이지 × 20건 = 300건 규모라 본문 스크래핑은 하지 않고 제목/요약/썸네일/발행시각만 담는다.
    /// </summary>
    public async Task<List<NewsItem>> GetKboNewsByDateAsync(DateOnly date, CancellationToken ct = default)
    {
        const int maxPages = 50; // 비정상 응답으로 무한 루프 도는 것 방지용 안전 상한
        var items = new List<NewsItem>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dateStr = date.ToString("yyyyMMdd");

        var totalPages = 1;
        for (var page = 1; page <= totalPages && page <= maxPages; page++)
        {
            ct.ThrowIfCancellationRequested();

            var listUrl = $"https://sports.news.naver.com/kbaseball/news/list?isphoto=N&date={dateStr}&page={page}";
            string json;
            try
            {
                json = await _http.GetStringAsync(listUrl, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[네이버 스포츠] {date} {page}페이지 목록 조회 실패, 해당 날짜 중단. Error: {msg}", date, page, ex.Message);
                break;
            }

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("totalPages", out var totalPagesEl))
            {
                totalPages = totalPagesEl.GetInt32();
            }

            if (!doc.RootElement.TryGetProperty("list", out var listEl)) break;

            foreach (var el in listEl.EnumerateArray())
            {
                var oid = el.TryGetProperty("oid", out var oidEl) ? oidEl.GetString() : null;
                var aid = el.TryGetProperty("aid", out var aidEl) ? aidEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(oid) || string.IsNullOrWhiteSpace(aid)) continue;

                var articleUrl = $"https://n.news.naver.com/sports/kbaseball/article/{oid}/{aid}";
                if (!seenUrls.Add(articleUrl)) continue;

                var title = el.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "";
                var summary = el.TryGetProperty("subContent", out var subEl) ? subEl.GetString() ?? "" : "";
                var thumbnail = el.TryGetProperty("thumbnail", out var thumbEl) ? thumbEl.GetString() : null;
                var officeName = el.TryGetProperty("officeName", out var officeEl) ? officeEl.GetString() ?? "네이버 스포츠" : "네이버 스포츠";

                // "2026.06.01 23:29" (KST) 형식
                var publishedAt = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(9));
                if (el.TryGetProperty("datetime", out var dtEl) &&
                    DateTime.TryParseExact(dtEl.GetString(), "yyyy.MM.dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                {
                    publishedAt = new DateTimeOffset(parsed, TimeSpan.FromHours(9));
                }

                items.Add(new NewsItem(
                    id: Guid.NewGuid().ToString(),
                    title: CleanHtml(title),
                    summary: CleanHtml(summary),
                    url: articleUrl,
                    publishedAt: publishedAt)
                {
                    Source = officeName,
                    Category = "스포츠",
                    Country = "한국",
                    ImageUrl = string.IsNullOrWhiteSpace(thumbnail) ? null : thumbnail
                });
            }

            await Task.Delay(150, ct);
        }

        _logger.LogInformation("[네이버 스포츠] {date} KBO 뉴스 {count}건 수집 (전체 {pages}페이지)", date, items.Count, totalPages);
        return items;
    }

    /// <summary>
    /// RawNews에서 본문(Content)이 비어 있는 네이버 기사들을 찾아 원문 페이지를 크롤링해 본문을 채운다.
    /// --backfill-news 소급 수집은 목록 정보만 담으므로(하루 300건 규모), 본문은 이 모드로 별도 채운다.
    /// - 배치 단위(20건)로 동시 5개까지 병렬 크롤링 후 한 트랜잭션으로 UPDATE — 중단돼도 재실행하면 이어서 진행
    /// - 페이지는 열렸는데 본문 영역을 못 찾은 기사(삭제/포토 전용 등)는 빈 문자열로 마킹해 무한 재시도를 막는다
    /// - 네트워크 오류는 NULL로 남겨서 다음 실행 때 다시 시도
    /// </summary>
    public async Task FillMissingContentAsync(string databasePath, CancellationToken ct = default)
    {
        const int batchSize = 20;
        const int maxConcurrency = 5;

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(ct);

        var targets = new List<(string Id, string Url, string? ImageUrl)>();
        var selectCmd = connection.CreateCommand();
        selectCmd.CommandText = """
            SELECT Id, Url, ImageUrl FROM RawNews
            WHERE Content IS NULL AND Url LIKE '%n.news.naver.com%'
            ORDER BY PublishedAt DESC;
            """;
        await using (var reader = await selectCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                targets.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        _logger.LogInformation("[뉴스 본문 채우기] 대상 {count}건 — 배치 {batch}건씩, 동시 {conc}개 크롤링 시작", targets.Count, batchSize, maxConcurrency);

        var processed = 0;
        var filled = 0;
        var failed = 0;
        using var semaphore = new SemaphoreSlim(maxConcurrency);

        foreach (var batch in targets.Chunk(batchSize))
        {
            ct.ThrowIfCancellationRequested();

            var tasks = batch.Select(async t =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    // 스포츠 기사(n.news.naver.com/sports/...)는 JS로 렌더링되는 SPA로 리다이렉트돼 HTML 스크래핑이
                    // 안 되고, 전용 JSON API가 본문 전체를 준다. URL에서 oid/aid를 뽑아 API를 우선 사용하고,
                    // 형식이 다른 URL(일반 뉴스)만 기존 HTML 스크래핑으로 폴백한다.
                    var oidAid = System.Text.RegularExpressions.Regex.Match(t.Url, @"article/(\d+)/(\d+)");
                    if (oidAid.Success)
                    {
                        var content = await FetchSportsArticleContentAsync(oidAid.Groups[1].Value, oidAid.Groups[2].Value, ct);
                        return (t.Id, Content: content, Image: (string?)null, ExistingImage: t.ImageUrl);
                    }

                    var (scraped, images) = await ScrapeNaverNewsAsync(t.Url, ct);
                    string? newContent = scraped.Length > 0 || images.Count > 0 ? scraped : null;
                    return (t.Id, Content: newContent, Image: images.FirstOrDefault(), ExistingImage: t.ImageUrl);
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            var results = await Task.WhenAll(tasks);

            await using var transaction = await connection.BeginTransactionAsync(ct);
            foreach (var r in results)
            {
                processed++;
                if (r.Content is null) { failed++; continue; }

                var updateCmd = connection.CreateCommand();
                updateCmd.Transaction = (SqliteTransaction)transaction;
                updateCmd.CommandText = """
                    UPDATE RawNews
                    SET Content = $content,
                        ImageUrl = COALESCE(ImageUrl, $imageUrl),
                        UpdatedAt = CURRENT_TIMESTAMP
                    WHERE Id = $id;
                    """;
                updateCmd.Parameters.AddWithValue("$content", r.Content);
                updateCmd.Parameters.AddWithValue("$imageUrl", (object?)r.Image ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("$id", r.Id);
                await updateCmd.ExecuteNonQueryAsync(ct);
                filled++;
            }
            await transaction.CommitAsync(ct);

            if (processed % 500 < batchSize)
            {
                _logger.LogInformation("[뉴스 본문 채우기] 진행 {processed}/{total} (채움 {filled}, 실패 {failed})", processed, targets.Count, filled, failed);
            }
        }

        _logger.LogInformation("[뉴스 본문 채우기] 완료 — 총 {processed}건 처리, 본문 저장 {filled}건, 실패(재시도 대상) {failed}건", processed, filled, failed);
    }

    /// <summary>
    /// 네이버 스포츠 기사 본문 API 호출. 성공 시 태그를 걷어낸 본문 텍스트,
    /// 기사 없음/삭제(정상 응답이지만 본문 없음)는 빈 문자열(재시도 안 함), 네트워크 오류는 null(재시도 대상).
    /// </summary>
    private async Task<string?> FetchSportsArticleContentAsync(string oid, string aid, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api-gw.sports.naver.com/news/article/{oid}/{aid}");
            request.Headers.Referrer = new Uri("https://m.sports.naver.com/");
            using var response = await _http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.NotFound) return string.Empty;
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var resultEl) ||
                !resultEl.TryGetProperty("articleInfo", out var infoEl) ||
                !infoEl.TryGetProperty("article", out var articleEl) ||
                !articleEl.TryGetProperty("content", out var contentEl))
            {
                return string.Empty;
            }

            var rawHtml = contentEl.GetString();
            if (string.IsNullOrWhiteSpace(rawHtml)) return string.Empty;

            // CleanHtml(InnerText)은 <br>을 그냥 삭제해 문단이 붙어버리므로 먼저 줄바꿈으로 치환한다
            var withLineBreaks = System.Text.RegularExpressions.Regex.Replace(rawHtml, @"<br\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return CleanHtml(withLineBreaks);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Sports article API failed for {oid}/{aid}: {msg}", oid, aid, ex.Message);
            return null;
        }
    }

    private async Task<(string Content, List<string> Images)> ScrapeNaverNewsAsync(string url, CancellationToken ct)
    {
        var images = new List<string>();
        var content = string.Empty;

        try
        {
            _logger.LogDebug("Scraping Naver news: {Url}", url);
            var html = await _http.GetStringAsync(url, ct);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // 1. 이미지 추출 (메타 태그 및 본문 이미지)
            // og:image 우선 추출
            var ogImage = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']")?.GetAttributeValue("content", null);
            if (!string.IsNullOrEmpty(ogImage)) images.Add(ogImage);

            // 2. 본문 영역 선택
            var contentNode = doc.DocumentNode.SelectSingleNode("//article[@id='dic_area']") 
                           ?? doc.DocumentNode.SelectSingleNode("//div[@id='newsct_article']")
                           ?? doc.DocumentNode.SelectSingleNode("//div[@id='articleBodyContents']");

            if (contentNode != null)
            {
                // 본문 내 이미지 추가 추출
                var imgNodes = contentNode.SelectNodes(".//img");
                if (imgNodes != null)
                {
                    foreach (var img in imgNodes)
                    {
                        var src = img.GetAttributeValue("data-src", null) ?? img.GetAttributeValue("src", null);
                        if (!string.IsNullOrEmpty(src) && !src.Contains("data:image") && !images.Contains(src))
                        {
                            images.Add(src);
                        }
                    }
                }

                // 불필요한 요소 제거
                var toRemove = contentNode.SelectNodes(".//script|.//style|.//comment()|.//div[contains(@class, 'reporter_area')]|.//div[contains(@class, 'copyright')]|.//span[contains(@class, 'end_photo_org')]");
                if (toRemove != null)
                {
                    foreach (var node in toRemove) node.Remove();
                }

                content = WebUtility.HtmlDecode(contentNode.InnerText).Trim();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to scrape Naver news from {Url}. Error: {Msg}", url, ex.Message);
        }

        return (content, images.Distinct().ToList());
    }

    private static string CleanHtml(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        // HtmlAgilityPack을 사용한 더 깔끔한 제거
        var doc = new HtmlDocument();
        doc.LoadHtml(input);
        return WebUtility.HtmlDecode(doc.DocumentNode.InnerText).Trim();
    }

    private static DateTimeOffset ParsePubDate(string pubDate)
    {
        if (DateTimeOffset.TryParse(pubDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
        {
            return result;
        }
        return DateTimeOffset.UtcNow;
    }
}
