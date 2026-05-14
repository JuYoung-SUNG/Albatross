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

    // 수집할 공개 RSS 피드 목록 (카테고리별, 국가별)
    private static readonly (string Source, string Url, string Category, string Country)[] RssFeeds =
    [
        // 한국
        ("조선일보", "https://www.chosun.com/arc/outboundfeeds/rss/category/politics/?outputType=xml", "정치", "한국"),
        ("조선일보", "https://www.chosun.com/arc/outboundfeeds/rss/category/national/?outputType=xml", "사회", "한국"),
        ("조선일보", "https://www.chosun.com/arc/outboundfeeds/rss/category/sports/?outputType=xml",   "스포츠", "한국"),
        ("조선일보", "https://www.chosun.com/arc/outboundfeeds/rss/category/entertainments/?outputType=xml", "연예", "한국"),
        ("조선일보", "https://www.chosun.com/arc/outboundfeeds/rss/category/technology/?outputType=xml", "IT", "한국"),
        ("매일경제", "https://www.mk.co.kr/rss/30000001/", "경제", "한국"),
        ("매일경제", "https://www.mk.co.kr/rss/50300009/", "연예", "한국"),
        
        // 미국
        ("CNN", "http://rss.cnn.com/rss/edition.rss", "일반", "미국"),
        ("NY Times", "https://rss.nytimes.com/services/xml/rss/nyt/Business.xml", "경제", "미국"),
        ("NY Times", "https://rss.nytimes.com/services/xml/rss/nyt/Technology.xml", "IT", "미국"),
        ("Hacker News", "https://hnrss.org/frontpage", "IT", "미국"),

        // 영국
        ("BBC", "http://feeds.bbci.co.uk/news/rss.xml", "일반", "영국"),
        ("BBC", "http://feeds.bbci.co.uk/news/business/rss.xml", "경제", "영국"),

        // 중국 (영문 서비스 위주)
        ("SCMP", "https://www.scmp.com/rss/2/feed", "일반", "중국"),
        ("SCMP", "https://www.scmp.com/rss/92/feed", "경제", "중국")
    ];

    public RssNewsScraperService(HttpClient http, ILogger<RssNewsScraperService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<IEnumerable<NewsItem>> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<NewsItem>();

        foreach (var (source, feedUrl, category, country) in RssFeeds)
        {
            try
            {
                var xml = await _http.GetStringAsync(feedUrl, cancellationToken);
                var items = ParseRss(xml, source, category, country);
                results.AddRange(items);
                _logger.LogInformation("Fetched {count} items from {source} ({category}/{country})", items.Count, source, category, country);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch RSS from {url}", feedUrl);
            }
        }

        return results
            .OrderByDescending(i => i.PublishedAt)
            .Take(200); // 데이터가 많아졌으므로 200개로 상향
    }

    private static List<NewsItem> ParseRss(string xml, string source, string category, string country)
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
                    Category = category,
                    Country = country
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
