using System.Net;
using System.Text;
using Albatross.Shared.Models;

namespace Albatross.Collector
{
    /// <summary>
    /// 골프 연습장 데이터로 "완성된 정적 HTML"을 생성한다.
    ///
    /// 왜 정적 생성인가 — 검색엔진이 받는 HTML에 본문이 그대로 들어 있어야 색인이 된다.
    /// Blazor WebAssembly는 첫 HTML이 빈 껍데기(로딩 스피너)라 검색 노출이 사실상 막힌다.
    /// 목록·상세 위주의 카탈로그 사이트는 정적 HTML이 SEO·속도 모두 유리하다.
    ///
    /// 출력물(public/)
    ///   index.html                 연습장 목록 (지역·유형 필터, JS 최소)
    ///   range/&lt;slug&gt;/index.html   연습장 상세 (+ JSON-LD 지역업체 스키마)
    ///   sitemap.xml, robots.txt    색인용
    /// </summary>
    internal static class GolfSiteGenerator
    {
        private const string SiteUrl = "https://albatrossgolf.pages.dev";
        private const string SiteName = "Albatross Golf";

        /// <summary>
        /// Google Search Console 소유권 확인용 값.
        /// Search Console → 속성 추가 → URL 접두어 → 소유권 확인 "HTML 태그"에서 나오는
        /// &lt;meta name="google-site-verification" content="..."&gt; 의 content 값만 여기에 넣는다.
        /// 비어 있으면 태그 자체를 넣지 않는다(빈 태그가 나가면 확인이 실패한다).
        /// 환경변수 GOOGLE_SITE_VERIFICATION 이 있으면 그 값이 우선한다.
        /// </summary>
        private static string GoogleSiteVerification =>
            Environment.GetEnvironmentVariable("GOOGLE_SITE_VERIFICATION") ?? "";

        public static async Task<int> GenerateAsync(List<GolfRangeDto> ranges, string outputDir, CancellationToken ct)
        {
            Directory.CreateDirectory(outputDir);

            await File.WriteAllTextAsync(Path.Combine(outputDir, "index.html"), BuildIndex(ranges), Encoding.UTF8, ct);

            foreach (var r in ranges)
            {
                var dir = Path.Combine(outputDir, "range", r.Slug);
                Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(Path.Combine(dir, "index.html"), BuildDetail(r), Encoding.UTF8, ct);
            }

            await File.WriteAllTextAsync(Path.Combine(outputDir, "sitemap.xml"), BuildSitemap(ranges), Encoding.UTF8, ct);
            await File.WriteAllTextAsync(Path.Combine(outputDir, "robots.txt"), BuildRobots(), Encoding.UTF8, ct);

            return ranges.Count + 1;
        }

        // ── 공통 레이아웃 ─────────────────────────────────────────────
        private static string Page(string title, string description, string canonicalPath, string bodyHtml, string extraHead = "")
        {
            var canonical = SiteUrl + canonicalPath;
            var verify = string.IsNullOrWhiteSpace(GoogleSiteVerification)
                ? ""
                : $"""<meta name="google-site-verification" content="{E(GoogleSiteVerification)}">{Environment.NewLine}""";
            return $"""
                <!doctype html>
                <html lang="ko">
                <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>{E(title)}</title>
                <meta name="description" content="{E(description)}">
                <link rel="canonical" href="{E(canonical)}">
                <meta property="og:type" content="website">
                <meta property="og:title" content="{E(title)}">
                <meta property="og:description" content="{E(description)}">
                <meta property="og:url" content="{E(canonical)}">
                <meta property="og:site_name" content="{E(SiteName)}">
                <link rel="stylesheet" href="/css/site.css">
                {verify}{extraHead}
                </head>
                <body>
                <header class="topbar">
                  <a class="brand" href="/">⛳ Albatross Golf</a>
                  <nav><a href="/">연습장 찾기</a><a href="https://albatross.pages.dev">Albatross 홈</a></nav>
                </header>
                <main>
                {bodyHtml}
                </main>
                <footer class="foot">
                  <p>요금·운영 정책은 변경될 수 있으니 방문 전 반드시 확인하세요.</p>
                  <p>© Albatross Golf</p>
                </footer>
                </body>
                </html>
                """;
        }

        // ── 목록 페이지 ───────────────────────────────────────────────
        private static string BuildIndex(List<GolfRangeDto> ranges)
        {
            var body = new StringBuilder();
            body.AppendLine("""<h1>수도권·전국 골프 연습장 정보</h1>""");
            body.AppendLine("""<p class="lede">파3·야외 인도어 연습장의 <strong>일일 요금, 좌타석 유무, 드라이버 사용 가능 여부, 주차</strong> 정보를 한곳에 정리했습니다.</p>""");

            if (ranges.Count == 0)
            {
                body.AppendLine("""<p class="empty">연습장 정보를 준비하고 있습니다.</p>""");
                return Page($"골프 연습장 정보 — {SiteName}",
                    "파3·야외 인도어 골프 연습장의 요금, 좌타석, 드라이버 사용 여부, 주차 정보를 정리했습니다.",
                    "/", body.ToString());
            }

            var regions = ranges.Select(r => r.Region).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
            var types = ranges.Select(r => r.Type).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();

            body.AppendLine("""<div class="filters" id="filters">""");
            body.AppendLine("""<div class="fgroup"><span class="flabel">지역</span><button class="fbtn on" data-f="region" data-v="">전체</button>""");
            foreach (var g in regions) body.AppendLine($"""<button class="fbtn" data-f="region" data-v="{E(g!)}">{E(g!)}</button>""");
            body.AppendLine("""</div>""");
            body.AppendLine("""<div class="fgroup"><span class="flabel">유형</span><button class="fbtn on" data-f="type" data-v="">전체</button>""");
            foreach (var t in types) body.AppendLine($"""<button class="fbtn" data-f="type" data-v="{E(t!)}">{E(t!)}</button>""");
            body.AppendLine("""</div></div>""");

            body.AppendLine("""
                <div class="tablewrap"><table class="ranges"><thead><tr>
                <th>연습장</th><th>지역</th><th>유형</th><th>일일 요금</th><th>좌타석</th><th>드라이버</th><th>주차</th>
                </tr></thead><tbody>
                """);

            foreach (var r in ranges.OrderBy(r => r.Region).ThenBy(r => r.City).ThenBy(r => r.Name))
            {
                body.AppendLine($"""
                    <tr data-region="{E(r.Region ?? "")}" data-type="{E(r.Type ?? "")}">
                      <td><a href="/range/{E(r.Slug)}/">{E(r.Name)}</a></td>
                      <td>{E(r.City ?? r.Region ?? "-")}</td>
                      <td>{E(r.Type ?? "-")}</td>
                      <td>{E(r.Price ?? "-")}</td>
                      <td>{E(r.LeftHanded ?? "-")}</td>
                      <td>{E(r.DriverAllowed ?? "-")}</td>
                      <td>{E(r.Parking ?? "-")}</td>
                    </tr>
                    """);
            }
            body.AppendLine("""</tbody></table></div>""");
            body.AppendLine("""<p class="count" id="count"></p>""");

            // 필터: 검색엔진은 위 표 전체를 그대로 읽고, 사용자는 JS로 걸러 본다
            body.AppendLine("""
                <script>
                (function(){
                  var sel={region:'',type:''};
                  var rows=[].slice.call(document.querySelectorAll('table.ranges tbody tr'));
                  var count=document.getElementById('count');
                  function apply(){
                    var n=0;
                    rows.forEach(function(tr){
                      var ok=(!sel.region||tr.dataset.region===sel.region)&&(!sel.type||tr.dataset.type===sel.type);
                      tr.style.display=ok?'':'none'; if(ok)n++;
                    });
                    count.textContent=n+'곳';
                  }
                  document.getElementById('filters').addEventListener('click',function(e){
                    var b=e.target.closest('.fbtn'); if(!b)return;
                    var f=b.dataset.f; sel[f]=b.dataset.v;
                    [].forEach.call(document.querySelectorAll('.fbtn[data-f="'+f+'"]'),function(x){x.classList.remove('on');});
                    b.classList.add('on'); apply();
                  });
                  apply();
                })();
                </script>
                """);

            var desc = $"수도권을 비롯한 골프 연습장 {ranges.Count}곳의 일일 요금, 좌타석, 드라이버 사용 여부, 주차 정보를 정리했습니다.";
            return Page($"골프 연습장 정보 {ranges.Count}곳 — {SiteName}", desc, "/", body.ToString());
        }

        // ── 상세 페이지 ───────────────────────────────────────────────
        private static string BuildDetail(GolfRangeDto r)
        {
            var body = new StringBuilder();

            body.AppendLine($"""<nav class="crumb"><a href="/">연습장 목록</a> › <span>{E(r.Name)}</span></nav>""");
            body.AppendLine($"""<h1>{E(r.Name)}</h1>""");

            body.Append("""<p class="chips">""");
            foreach (var chip in new[] { r.Type, r.Region, r.City })
                if (!string.IsNullOrWhiteSpace(chip)) body.Append($"""<span class="chip">{E(chip!)}</span>""");
            body.AppendLine("""</p>""");

            if (!string.IsNullOrWhiteSpace(r.Summary))
                body.AppendLine($"""<p class="lede">{E(r.Summary!)}</p>""");

            // 핵심 정보 표 — 검색 사용자가 가장 궁금해하는 항목
            body.AppendLine("""<table class="facts"><tbody>""");
            void Row(string label, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    body.AppendLine($"""<tr><th>{E(label)}</th><td>{E(value!)}</td></tr>""");
            }
            Row("일일 타석 요금", r.Price);
            Row("좌타석", r.LeftHanded);
            Row("드라이버 사용", r.DriverAllowed);
            Row("주차", r.Parking);
            Row("영업시간", r.Hours);
            Row("주소", r.Address);
            Row("전화", r.Phone);
            body.AppendLine("""</tbody></table>""");

            if (r.Highlights.Count > 0)
            {
                body.AppendLine("""<h2>이런 점이 좋아요</h2><ul class="good">""");
                foreach (var h in r.Highlights) body.AppendLine($"<li>{E(h)}</li>");
                body.AppendLine("</ul>");
            }

            if (r.Cautions.Count > 0)
            {
                body.AppendLine("""<h2>미리 알아두세요</h2><ul class="caution">""");
                foreach (var c in r.Cautions) body.AppendLine($"<li>{E(c)}</li>");
                body.AppendLine("</ul>");
            }

            if (r.SourceUrls.Count > 0)
            {
                body.AppendLine("""<h2>참고한 곳</h2><ul class="sources">""");
                foreach (var u in r.SourceUrls)
                    body.AppendLine($"""<li><a href="{E(u)}" target="_blank" rel="noopener nofollow">{E(u)}</a></li>""");
                body.AppendLine("</ul>");
            }

            body.AppendLine($"""<p class="asof">정보 기준: {E(r.UpdatedAt ?? "-")}</p>""");

            // 구조화 데이터 — 구글이 지역 업체로 이해하면 지도·리치결과에 노출될 수 있다
            var jsonLd = BuildJsonLd(r);
            var title = $"{r.Name} 요금·좌타석·주차 정보 — {SiteName}";
            var desc = BuildDetailDescription(r);
            return Page(title, desc, $"/range/{r.Slug}/", body.ToString(), jsonLd);
        }

        private static string BuildDetailDescription(GolfRangeDto r)
        {
            var parts = new List<string> { $"{r.City ?? r.Region ?? ""} {r.Name}".Trim() };
            if (!string.IsNullOrWhiteSpace(r.Price)) parts.Add($"요금 {r.Price}");
            if (!string.IsNullOrWhiteSpace(r.LeftHanded)) parts.Add($"좌타석 {r.LeftHanded}");
            if (!string.IsNullOrWhiteSpace(r.DriverAllowed)) parts.Add($"드라이버 {r.DriverAllowed}");
            if (!string.IsNullOrWhiteSpace(r.Parking)) parts.Add($"주차 {r.Parking}");
            var text = string.Join(" · ", parts);
            return text.Length > 155 ? text[..152] + "..." : text;
        }

        private static string BuildJsonLd(GolfRangeDto r)
        {
            static string J(string? s) => System.Text.Json.JsonSerializer.Serialize(s ?? string.Empty);

            var sb = new StringBuilder();
            sb.AppendLine("""<script type="application/ld+json">""");
            sb.AppendLine("{");
            sb.AppendLine(""" "@context": "https://schema.org",""");
            sb.AppendLine(""" "@type": "SportsActivityLocation",""");
            sb.AppendLine($""" "name": {J(r.Name)},""");
            sb.AppendLine($""" "url": {J($"{SiteUrl}/range/{r.Slug}/")},""");
            if (!string.IsNullOrWhiteSpace(r.Phone)) sb.AppendLine($""" "telephone": {J(r.Phone)},""");
            if (!string.IsNullOrWhiteSpace(r.Hours)) sb.AppendLine($""" "openingHours": {J(r.Hours)},""");
            if (!string.IsNullOrWhiteSpace(r.Address))
            {
                sb.AppendLine(""" "address": {""");
                sb.AppendLine("""   "@type": "PostalAddress",""");
                sb.AppendLine($"""   "addressCountry": "KR",""");
                if (!string.IsNullOrWhiteSpace(r.City)) sb.AppendLine($"""   "addressLocality": {J(r.City)},""");
                sb.AppendLine($"""   "streetAddress": {J(r.Address)}""");
                sb.AppendLine(" },");
            }
            sb.AppendLine($""" "description": {J(r.Summary ?? r.Name)}""");
            sb.AppendLine("}");
            sb.AppendLine("</script>");
            return sb.ToString();
        }

        // ── 색인용 파일 ───────────────────────────────────────────────
        private static string BuildSitemap(List<GolfRangeDto> ranges)
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var sb = new StringBuilder();
            sb.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
            sb.AppendLine("""<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">""");
            sb.AppendLine($"  <url><loc>{SiteUrl}/</loc><lastmod>{today}</lastmod><changefreq>weekly</changefreq><priority>1.0</priority></url>");
            foreach (var r in ranges)
            {
                var lastmod = string.IsNullOrWhiteSpace(r.UpdatedAt) ? today : r.UpdatedAt;
                sb.AppendLine($"  <url><loc>{SiteUrl}/range/{r.Slug}/</loc><lastmod>{lastmod}</lastmod><changefreq>monthly</changefreq><priority>0.8</priority></url>");
            }
            sb.AppendLine("</urlset>");
            return sb.ToString();
        }

        private static string BuildRobots() => $"""
            User-agent: *
            Allow: /

            Sitemap: {SiteUrl}/sitemap.xml
            """;

        private static string E(string s) => WebUtility.HtmlEncode(s ?? string.Empty);
    }
}
