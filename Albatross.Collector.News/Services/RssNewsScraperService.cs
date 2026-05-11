using System.Xml.Linq;
using Albatross.Collector.News.Models;
using Microsoft.Extensions.Logging;

namespace Albatross.Collector.News.Services;

/// <summary>
/// RSS 피드를 HtmlAgilityPack으로 파싱해 뉴스 아이템을 수집합니다.
/// NewsApi:ApiKey 가 없어도 동작합니다.
/// </summary>
public class RssNewsScraperService : INewsService
{
    private readonly HttpClient _http;
    private readonly ILogger<RssNewsScraperService> _logger;

    // 수집할 공개 RSS 피드 목록 (카테고리별)
    private static readonly (string Source, string Url, string Category)[] RssFeeds =
    [
        ("조선일보", "https://www.chosun.com/arc/outboundfeeds/rss/category/politics/?outputType=xml", "정치"),
        ("조선일보", "https://www.chosun.com/arc/outboundfeeds/rss/category/national/?outputType=xml", "사회"),
        ("조선일보", "https://www.chosun.com/arc/outboundfeeds/rss/category/sports/?outputType=xml",   "스포츠"),
        ("조선일보", "https://www.chosun.com/arc/outboundfeeds/rss/category/technology/?outputType=xml", "IT/기술"),
        ("Hacker News", "https://hnrss.org/frontpage", "해외/IT"),
    ];

    public RssNewsScraperService(HttpClient http, ILogger<RssNewsScraperService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<IEnumerable<NewsItem>> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<NewsItem>();

        foreach (var (source, feedUrl, category) in RssFeeds)
        {
            try
            {
                var xml = await _http.GetStringAsync(feedUrl, cancellationToken);
                var items = ParseRss(xml, source, category);
                results.AddRange(items);
                _logger.LogInformation("Fetched {count} items from {source} ({category})", items.Count, source, category);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch RSS from {url}", feedUrl);
            }
        }

        return results
            .OrderByDescending(i => i.PublishedAt)
            .Take(100); // 카테고리가 많아졌으므로 100개로 상향
    }

    private static List<NewsItem> ParseRss(string xml, string source, string category)
    {
        var doc = XDocument.Parse(xml);
        XNamespace? dc = "http://purl.org/dc/elements/1.1/";

        return doc.Descendants("item")
            .Select(item =>
            {
                var title       = item.Element("title")?.Value ?? "(제목 없음)";
                var link        = item.Element("link")?.Value ?? string.Empty;
                var description = item.Element("description")?.Value ?? string.Empty;
                var pubDateStr  = item.Element("pubDate")?.Value;
                var guid        = item.Element("guid")?.Value ?? link;

                // description 에서 HTML 태그 제거
                var cleanSummary = HtmlStrip(description);

                DateTimeOffset publishedAt = pubDateStr is not null
                    && DateTimeOffset.TryParse(pubDateStr, out var dt)
                    ? dt : DateTimeOffset.UtcNow;

                return new NewsItem(guid, title, cleanSummary, link, publishedAt)
                {
                    Source = source,
                    Category = category
                };
            })
            .ToList();
    }

    /// <summary>HtmlAgilityPack을 사용해 HTML 태그를 제거합니다.</summary>
    private static string HtmlStrip(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return html;
        var hap = new HtmlAgilityPack.HtmlDocument();
        hap.LoadHtml(html);
        var text = hap.DocumentNode.InnerText;
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }
}
