using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Albatross.Collector.News.Models;
using Microsoft.Extensions.Logging;

namespace Albatross.Collector.News.Services
{
    public class RssNewsScraperService : INewsService
    {
        private readonly HttpClient _http;
        private readonly ILogger<RssNewsScraperService> _logger;

        // 수집할 공개 RSS 피드 목록
        private static readonly (string Source, string Url, string Category, string Country)[] RssFeeds =
        [
            ("조선일보", "https://www.chosun.com/arc/outboundfeeds/rss/category/politics/?outputType=xml", "정치", "한국"),
            ("조선일보", "https://www.chosun.com/arc/outboundfeeds/rss/category/national/?outputType=xml", "사회", "한국"),
            ("조선일보", "https://www.chosun.com/arc/outboundfeeds/rss/category/sports/?outputType=xml",   "스포츠", "한국"),
            ("조선일보", "https://www.chosun.com/arc/outboundfeeds/rss/category/entertainments/?outputType=xml", "연예", "한국"),
            ("조선일보", "https://www.chosun.com/arc/outboundfeeds/rss/category/technology/?outputType=xml", "IT", "한국"),
            ("매일경제", "https://www.mk.co.kr/rss/30000001/", "경제", "한국"),
            ("매일경제", "https://www.mk.co.kr/rss/50300009/", "연예", "한국"),

            ("CNN", "http://rss.cnn.com/rss/edition.rss", "일반", "미국"),
            ("NY Times", "https://rss.nytimes.com/services/xml/rss/nyt/Business.xml", "경제", "미국"),
            ("NY Times", "https://rss.nytimes.com/services/xml/rss/nyt/Technology.xml", "IT", "미국"),
            ("Hacker News", "https://hnrss.org/frontpage", "IT", "미국"),

            ("BBC", "http://feeds.bbci.co.uk/news/rss.xml", "일반", "영국"),
            ("BBC", "http://feeds.bbci.co.uk/news/business/rss.xml", "경제", "영국"),

            ("SCMP", "https://www.scmp.com/rss/2/feed", "일반", "중국"),
            ("SCMP", "https://www.scmp.com/rss/92/feed", "경제", "중국")
        ];

        public RssNewsScraperService(HttpClient http, ILogger<RssNewsScraperService> logger)
        {
            _http = http;
            _logger = logger;

            // 브라우저 봇 차단 우회용 User-Agent 기본 헤더 추가
            _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }

        public async Task<IEnumerable<NewsItem>> GetLatestAsync(CancellationToken cancellationToken = default)
        {
            var results = new List<NewsItem>();

            // ⏰ [시간 필터 설정] 현재 시간 기준 딱 '1시간 전' 타임스탬프 계산
            DateTimeOffset timeCutoff = DateTimeOffset.Now.AddHours(-1);
            _logger.LogInformation("⏳ 시간 필터링 활성화: {time} 이후에 발행된 뉴스만 골라냅니다.", timeCutoff.ToString("yyyy-MM-dd HH:mm:ss"));

            foreach (var (source, feedUrl, category, country) in RssFeeds)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(5));

                try
                {
                    _logger.LogInformation("Requesting RSS from {source} -> {url}", source, feedUrl);

                    var xml = await _http.GetStringAsync(feedUrl, cts.Token);
                    var allItems = ParseRss(xml, source, category, country);

                    // 🎯 [핵심 필터 적용] 가져온 뉴스 중 1시간 이내 뉴스만 LINQ로 쏙 골라내기
                    var freshItems = allItems.Where(item => item.PublishedAt >= timeCutoff).ToList();

                    results.AddRange(freshItems);

                    _logger.LogInformation("Successfully Fetched {count} items (Filtered from {total}) from {source}", freshItems.Count, allItems.Count, source);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Timeout (5s) exceeded while fetching RSS from {source}", source);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to fetch RSS from {source}. Error: {msg}", source, ex.Message);
                }
            }

            return results
                .OrderByDescending(i => i.PublishedAt)
                .Take(200);
        }

        private static List<NewsItem> ParseRss(string xml, string source, string category, string country)
        {
            var doc = XDocument.Parse(xml);
            XNamespace media = "http://search.yahoo.com/mrss/";

            return doc.Descendants("item")
                .Select(item =>
                {
                    var title = item.Element("title")?.Value ?? "(제목 없음)";
                    var link = item.Element("link")?.Value ?? string.Empty;
                    var description = item.Element("description")?.Value ?? string.Empty;
                    var pubDateStr = item.Element("pubDate")?.Value;
                    var guid = item.Element("guid")?.Value ?? link;

                    string? imageUrl = null;

                    var mediaContent = item.Elements(media + "content")
                                           .FirstOrDefault(x => x.Attribute("medium")?.Value == "image" || x.Attribute("type")?.Value.StartsWith("image/") == true);
                    if (mediaContent != null)
                    {
                        imageUrl = mediaContent.Attribute("url")?.Value;
                    }

                    if (string.IsNullOrEmpty(imageUrl))
                    {
                        var enclosure = item.Elements("enclosure")
                                            .FirstOrDefault(x => x.Attribute("type")?.Value.StartsWith("image/") == true);
                        if (enclosure != null)
                        {
                            imageUrl = enclosure.Attribute("url")?.Value;
                        }
                    }

                    if (string.IsNullOrEmpty(imageUrl) && !string.IsNullOrWhiteSpace(description))
                    {
                        var hap = new HtmlAgilityPack.HtmlDocument();
                        hap.LoadHtml(description);
                        var img = hap.DocumentNode.SelectSingleNode("//img");
                        if (img != null)
                        {
                            imageUrl = img.GetAttributeValue("src", null);
                        }
                    }

                    var cleanSummary = HtmlStrip(description);

                    // 각 언론사별 시차(Timezone) 정보를 포함하여 안전하게 DateTimeOffset으로 변환
                    // 1. 우선 RSS feed에 적힌 날짜 문자열을 읽어옵니다.
                    DateTimeOffset publishedAt;

                    if (pubDateStr is not null && DateTimeOffset.TryParse(pubDateStr, out var parsedDate))
                    {
                        // 2. 🌟 핵심: 읽어온 날짜가 어떤 표준시든 관계없이 한국 시간(+09:00) 체계로 강제 변환합니다.
                        publishedAt = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(parsedDate, "Korea Standard Time");
                    }
                    else
                    {
                        // 날짜가 없거나 파싱에 실패하면 현재 한국 시간으로 대체합니다.
                        publishedAt = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Korea Standard Time");
                    }

                    return new NewsItem(guid, title, cleanSummary, link, publishedAt)
                    {
                        Source = source,
                        Category = category,
                        Country = country,
                        ImageUrl = imageUrl
                    };
                })
                .ToList();
        }

        private static string HtmlStrip(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return html;
            var hap = new HtmlAgilityPack.HtmlDocument();
            hap.LoadHtml(html);
            var text = hap.DocumentNode.InnerText;
            return System.Net.WebUtility.HtmlDecode(text).Trim();
        }
    }
}