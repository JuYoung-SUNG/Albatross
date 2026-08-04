using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Albatross.Collector.News.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Albatross.Collector.News.Services
{
    public class RssNewsScraperService : INewsService
    {
        private readonly HttpClient _http;
        private readonly ILogger<RssNewsScraperService> _logger;
        private readonly IConfiguration _config;

        // 수집할 공개 RSS 피드 목록 — 정치/사회/경제/IT/연예/스포츠/문화/세계 등 전 범위를 여러 매체에서 폭넓게 수집한다.
        // (2026-07 기준 실제 응답을 확인한 피드만 포함. 죽은 피드는 개별 try/catch로 걸러지므로 전체 수집엔 영향 없음)
        private static readonly (string Source, string Url, string Category, string Country)[] RssFeeds =
        [
            // ── 연합뉴스 (피드당 최대 120건) ──
            ("연합뉴스", "https://www.yna.co.kr/rss/politics.xml",      "정치",   "한국"),
            ("연합뉴스", "https://www.yna.co.kr/rss/economy.xml",       "경제",   "한국"),
            ("연합뉴스", "https://www.yna.co.kr/rss/society.xml",       "사회",   "한국"),
            ("연합뉴스", "https://www.yna.co.kr/rss/industry.xml",      "산업",   "한국"),
            ("연합뉴스", "https://www.yna.co.kr/rss/international.xml",  "세계",   "한국"),
            ("연합뉴스", "https://www.yna.co.kr/rss/culture.xml",       "문화",   "한국"),
            ("연합뉴스", "https://www.yna.co.kr/rss/sports.xml",        "스포츠", "한국"),

            // ── 한국경제 ──
            ("한국경제", "https://www.hankyung.com/feed/all-news",       "일반", "한국"),
            ("한국경제", "https://www.hankyung.com/feed/economy",        "경제", "한국"),
            ("한국경제", "https://www.hankyung.com/feed/politics",       "정치", "한국"),
            ("한국경제", "https://www.hankyung.com/feed/it",             "IT",   "한국"),

            // ── 동아일보 ──
            ("동아일보", "https://rss.donga.com/total.xml",     "일반", "한국"),
            ("동아일보", "https://rss.donga.com/politics.xml",  "정치", "한국"),
            ("동아일보", "https://rss.donga.com/economy.xml",   "경제", "한국"),

            // ── 경향신문 ──
            ("경향신문", "https://www.khan.co.kr/rss/rssdata/total_news.xml",   "일반", "한국"),
            ("경향신문", "https://www.khan.co.kr/rss/rssdata/politic_news.xml", "정치", "한국"),
            ("경향신문", "https://www.khan.co.kr/rss/rssdata/economy_news.xml", "경제", "한국"),

            // ── 조선일보 ──
            ("조선일보", "https://www.chosun.com/arc/outboundfeeds/rss/category/politics/?outputType=xml", "정치", "한국"),
            ("조선일보", "https://www.chosun.com/arc/outboundfeeds/rss/category/national/?outputType=xml", "사회", "한국"),
            ("조선일보", "https://www.chosun.com/arc/outboundfeeds/rss/category/sports/?outputType=xml",   "스포츠", "한국"),
            ("조선일보", "https://www.chosun.com/arc/outboundfeeds/rss/category/entertainments/?outputType=xml", "연예", "한국"),
            ("조선일보", "https://www.chosun.com/arc/outboundfeeds/rss/category/technology/?outputType=xml", "IT", "한국"),

            // ── 매일경제 ──
            ("매일경제", "https://www.mk.co.kr/rss/30000001/", "경제", "한국"),
            ("매일경제", "https://www.mk.co.kr/rss/50300009/", "연예", "한국"),

            // ── SBS 뉴스 ──
            ("SBS", "https://news.sbs.co.kr/news/SectionRssFeed.do?sectionId=01", "일반", "한국"),
            ("SBS", "https://news.sbs.co.kr/news/SectionRssFeed.do?sectionId=02", "정치", "한국"),
            ("SBS", "https://news.sbs.co.kr/news/SectionRssFeed.do?sectionId=03", "경제", "한국"),

            // ── 기타 종합/전문 매체 ──
            ("한겨레",   "http://www.hani.co.kr/rss/",                                  "일반", "한국"),
            ("세계일보", "https://www.segye.com/Articles/RSSList/segye_recent.xml",     "일반", "한국"),
            ("JTBC",     "https://fs.jtbc.co.kr/RSS/newsflash.xml",                     "일반", "한국"),
            ("노컷뉴스", "https://rss.nocutnews.co.kr/nocutNews.xml",                   "일반", "한국"),
            ("ZDNet",    "https://feeds.feedburner.com/zdkorea",                        "IT",   "한국"),

            // ── 해외 매체 ──
            ("CNN", "http://rss.cnn.com/rss/edition.rss", "일반", "미국"),
            ("NY Times", "https://rss.nytimes.com/services/xml/rss/nyt/Business.xml", "경제", "미국"),
            ("NY Times", "https://rss.nytimes.com/services/xml/rss/nyt/Technology.xml", "IT", "미국"),
            ("Hacker News", "https://hnrss.org/frontpage", "IT", "미국"),
            ("BBC", "http://feeds.bbci.co.uk/news/rss.xml", "일반", "영국"),
            ("BBC", "http://feeds.bbci.co.uk/news/business/rss.xml", "경제", "영국"),
            ("SCMP", "https://www.scmp.com/rss/2/feed/", "일반", "중국"),
            ("SCMP", "https://www.scmp.com/rss/92/feed/", "경제", "중국")
        ];

        public RssNewsScraperService(HttpClient http, ILogger<RssNewsScraperService> logger, IConfiguration config)
        {
            _http = http;
            _logger = logger;
            _config = config;

            // 브라우저 봇 차단 우회용 User-Agent 기본 헤더 추가
            _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }

        public async Task<IEnumerable<NewsItem>> GetLatestAsync(CancellationToken cancellationToken = default)
        {
            var results = new List<NewsItem>();

            // ⏰ [시간 필터 설정] Collector:RecencyMinutes 설정값 기준으로 타임스탬프 계산 (네이버 뉴스와 동일한 설정 공유)
            var recencyMinutes = _config.GetValue<int>("Collector:RecencyMinutes", 10);
            DateTimeOffset timeCutoff = DateTimeOffset.Now.AddMinutes(-recencyMinutes);
            _logger.LogInformation("⏳ 시간 필터링 활성화: {time} 이후에 발행된 뉴스만 골라냅니다.", timeCutoff.ToString("yyyy-MM-dd HH:mm:ss"));

            foreach (var (source, feedUrl, category, country) in RssFeeds)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                // 본문 스크래핑을 제거해 피드 XML 다운로드만 하므로, 대용량 피드(연합뉴스 120건 등)가
                // 5초 안에 못 끝나 타임아웃되던 문제를 줄이기 위해 10초로 늘린다.
                cts.CancelAfter(TimeSpan.FromSeconds(10));

                try
                {
                    _logger.LogInformation("Requesting RSS from {source} -> {url}", source, feedUrl);

                    var xml = await _http.GetStringAsync(feedUrl, cts.Token);
                    var allItems = ParseRss(xml, source, category, country);

                    // 🎯 [핵심 필터 적용] 가져온 뉴스 중 설정된 최근성(기본 60분) 이내 뉴스만 LINQ로 쏙 골라내기
                    var freshItems = allItems.Where(item => item.PublishedAt >= timeCutoff).ToList();

                    // [임시 비활성화] 기사 페이지를 직접 열어 본문을 별도로 긁어오는 로직은 현재 사용하지 않는다.
                    // (한 시간마다 최대한 많은 기사 "목록"만 빠르게 수집하는 것이 목적이라 개별 본문 크롤링은 생략)
                    // foreach (var item in freshItems)
                    // {
                    //     if (!string.IsNullOrWhiteSpace(item.Content) || string.IsNullOrWhiteSpace(item.Url))
                    //     {
                    //         continue;
                    //     }
                    //
                    //     item.Content = await ScrapeArticleBodyAsync(item.Url, cancellationToken);
                    // }

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

            // 최대한 많은 뉴스를 수집하는 것이 목적이므로 상한을 넉넉히 둔다 (한 시간치 전 매체 합산 대비)
            return results
                .OrderByDescending(i => i.PublishedAt)
                .Take(2000);
        }

        private static List<NewsItem> ParseRss(string xml, string source, string category, string country)
        {
            var doc = XDocument.Parse(xml);
            XNamespace media = "http://search.yahoo.com/mrss/";
            XNamespace contentNs = "http://purl.org/rss/1.0/modules/content/";

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

                    // 일부 피드(조선일보 등)는 <content:encoded>에 본문 전체를 담아 보내므로 그대로 활용
                    var encodedContent = item.Element(contentNs + "encoded")?.Value;
                    var fullContent = string.IsNullOrWhiteSpace(encodedContent) ? null : HtmlStrip(encodedContent);

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
                        ImageUrl = imageUrl,
                        Content = fullContent
                    };
                })
                .ToList();
        }

        private async Task<string?> ScrapeArticleBodyAsync(string url, CancellationToken cancellationToken)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                var html = await _http.GetStringAsync(url, cts.Token);
                var doc = new HtmlAgilityPack.HtmlDocument();
                doc.LoadHtml(html);

                // 매일경제 등 국내 언론사가 흔히 쓰는 articleBody 구조를 우선 시도,
                // 없으면 일반적인 <article> 태그로 폴백 (실패 시 요약만 사용)
                var contentNode = doc.DocumentNode.SelectSingleNode("//*[@itemprop='articleBody']")
                               ?? doc.DocumentNode.SelectSingleNode("//div[contains(@class,'news_cnt_detail_wrap')]")
                               ?? doc.DocumentNode.SelectSingleNode("//article");

                if (contentNode == null) return null;

                var toRemove = contentNode.SelectNodes(".//script|.//style|.//comment()");
                if (toRemove != null)
                {
                    foreach (var node in toRemove) node.Remove();
                }

                var text = WebUtility.HtmlDecode(contentNode.InnerText).Trim();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Failed to scrape article body from {url}. Error: {msg}", url, ex.Message);
                return null;
            }
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