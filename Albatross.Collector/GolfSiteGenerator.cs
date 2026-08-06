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
                  <a class="brand" href="/"><span class="mark">⛳</span> Albatross <em>Golf</em></a>
                  <nav><a href="/">연습장 찾기</a><a href="https://albatross.pages.dev">Albatross 홈</a></nav>
                </header>
                <main>
                {bodyHtml}
                </main>
                <footer class="foot">
                  <p class="warn">요금과 운영 정책은 자주 바뀝니다. <strong>방문 전 전화로 한 번 더 확인하세요.</strong></p>
                  <p>정보는 각 시설의 공식 안내를 기준으로 정리하며, 확인되지 않은 항목은 채우지 않고 <em>미확인</em>으로 둡니다.</p>
                  <p class="copy">© Albatross Golf</p>
                </footer>
                </body>
                </html>
                """;
        }

        // ── 목록 페이지 ───────────────────────────────────────────────
        private static string BuildIndex(List<GolfRangeDto> ranges)
        {
            var body = new StringBuilder();

            if (ranges.Count == 0)
            {
                body.AppendLine("""
                    <section class="hero">
                      <p class="kicker">수도권 골프 연습장</p>
                      <h1>연습장 갈 때마다 매번 전화로 물어보던 것들</h1>
                    </section>
                    <p class="empty">연습장 정보를 준비하고 있습니다.</p>
                    """);
                return Page($"골프 연습장 정보 — {SiteName}",
                    "파3·야외 인도어 골프 연습장의 요금, 좌타석, 드라이버 사용 여부, 주차 정보를 정리했습니다.",
                    "/", body.ToString());
            }

            var sorted = ranges.OrderBy(r => r.City).ThenBy(r => r.Name).ToList();
            var cities = sorted.Select(r => CityGroup(r)).Where(x => x.Length > 0).Distinct().ToList();
            var types = sorted.Select(r => r.Type).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();

            // 히어로 — 이 사이트가 답해주는 것을 먼저 말한다
            body.AppendLine($"""
                <section class="hero">
                  <p class="kicker">수도권 골프 연습장 {sorted.Count}곳</p>
                  <h1>연습장 갈 때마다<br>매번 전화로 물어보던 것들</h1>
                  <p class="lede">요금이 얼마인지, 좌타석은 있는지, 드라이버를 쳐도 되는지, 주차는 되는지.
                     공식 안내를 확인해 한곳에 정리했습니다.</p>
                  <ul class="answers">
                    <li><span>₩</span>일일 요금</li>
                    <li><span>↰</span>좌타석</li>
                    <li><span>🏌</span>드라이버</li>
                    <li><span>P</span>주차</li>
                  </ul>
                </section>
                """);

            // 필터 — 검색엔진은 아래 카드 전체를 그대로 읽고, 사용자는 JS로 걸러 본다
            body.AppendLine("""<section class="toolbar" id="filters">""");
            body.AppendLine("""<div class="fgroup"><span class="flabel">지역</span><button class="fbtn on" data-f="city" data-v="">전체</button>""");
            foreach (var c in cities) body.AppendLine($"""<button class="fbtn" data-f="city" data-v="{E(c)}">{E(c)}</button>""");
            body.AppendLine("""</div>""");
            body.AppendLine("""<div class="fgroup"><span class="flabel">유형</span><button class="fbtn on" data-f="type" data-v="">전체</button>""");
            foreach (var t in types) body.AppendLine($"""<button class="fbtn" data-f="type" data-v="{E(t!)}">{E(t!)}</button>""");
            body.AppendLine("""</div>""");
            body.AppendLine("""
                <label class="search"><input type="search" id="q" placeholder="연습장 이름이나 지역으로 찾기" autocomplete="off"></label>
                </section>
                <p class="count" id="count"></p>
                """);

            body.AppendLine("""<div class="grid" id="grid">""");
            foreach (var r in sorted) body.AppendLine(BuildCard(r));
            body.AppendLine("""</div>""");
            body.AppendLine("""<p class="noresult" id="noresult" hidden>조건에 맞는 연습장이 없습니다.</p>""");

            body.AppendLine("""
                <script>
                (function(){
                  var sel={city:'',type:''}, q='';
                  var cards=[].slice.call(document.querySelectorAll('.card'));
                  var count=document.getElementById('count'), none=document.getElementById('noresult');
                  function apply(){
                    var n=0;
                    cards.forEach(function(el){
                      var ok=(!sel.city||el.dataset.city===sel.city)
                          && (!sel.type||el.dataset.type===sel.type)
                          && (!q||el.dataset.search.indexOf(q)>-1);
                      el.hidden=!ok; if(ok)n++;
                    });
                    count.textContent=n+'곳';
                    none.hidden=n>0;
                  }
                  document.getElementById('filters').addEventListener('click',function(e){
                    var b=e.target.closest('.fbtn'); if(!b)return;
                    var f=b.dataset.f; sel[f]=b.dataset.v;
                    [].forEach.call(document.querySelectorAll('.fbtn[data-f="'+f+'"]'),function(x){x.classList.remove('on');});
                    b.classList.add('on'); apply();
                  });
                  document.getElementById('q').addEventListener('input',function(e){
                    q=e.target.value.trim().toLowerCase(); apply();
                  });
                  apply();
                })();
                </script>
                """);

            var desc = $"수도권 골프 연습장 {sorted.Count}곳의 일일 요금, 좌타석, 드라이버 사용 여부, 주차 정보를 공식 안내 기준으로 정리했습니다.";
            return Page($"수도권 골프 연습장 {sorted.Count}곳 요금·좌타석 정보 — {SiteName}", desc, "/", body.ToString());
        }

        /// <summary>목록 카드 하나. 확인된 정보는 강조하고 미확인은 눈에 덜 띄게 둔다.</summary>
        private static string BuildCard(GolfRangeDto r)
        {
            var search = $"{r.Name} {r.City} {r.Region} {r.Type} {r.Address}".ToLowerInvariant();
            var sb = new StringBuilder();

            sb.AppendLine($"""
                <article class="card" data-city="{E(CityGroup(r))}" data-type="{E(r.Type ?? "")}" data-search="{E(search)}">
                  <a class="cardlink" href="/range/{E(r.Slug)}/">
                  <header>
                    <h2>{E(r.Name)}</h2>
                    <p class="where">{E(r.City ?? r.Region ?? "")}{(string.IsNullOrWhiteSpace(r.Type) ? "" : $" · {E(r.Type!)}")}</p>
                  </header>
                """);

            sb.AppendLine(IsUnknown(r.Price)
                ? """<p class="price none">요금 미확인</p>"""
                : $"""<p class="price">{E(r.Price!)}</p>""");

            sb.AppendLine("""<dl class="facts">""");
            sb.AppendLine(Fact("좌타석", r.LeftHanded));
            sb.AppendLine(Fact("드라이버", r.DriverAllowed));
            sb.AppendLine(Fact("주차", r.Parking));
            sb.AppendLine("""</dl>""");

            sb.AppendLine("""<span class="more">자세히 보기</span></a></article>""");
            return sb.ToString();

            static string Fact(string label, string? value) => IsUnknown(value)
                ? $"""<div><dt>{E(label)}</dt><dd class="unk">미확인</dd></div>"""
                : $"""<div><dt>{E(label)}</dt><dd>{E(Shorten(value!))}</dd></div>""";
        }

        /// <summary>카드에서는 값이 길면 잘라 보여준다 (상세 페이지에 전문이 있다).</summary>
        private static string Shorten(string s, int max = 22)
        {
            s = s.Trim();
            var cut = s.IndexOf(" (", StringComparison.Ordinal);
            if (cut > 3) s = s[..cut];
            return s.Length > max ? s[..(max - 1)] + "…" : s;
        }

        private static bool IsUnknown(string? v) =>
            string.IsNullOrWhiteSpace(v) || v.TrimStart().StartsWith("미확인", StringComparison.Ordinal);

        /// <summary>필터용 지역 묶음. "경기 용인시" → "경기", "인천 서구" → "인천", "서울 강북구" → "서울".</summary>
        private static string CityGroup(GolfRangeDto r)
        {
            var city = r.City?.Trim();
            if (string.IsNullOrWhiteSpace(city)) return r.Region?.Trim() ?? "";
            var space = city.IndexOf(' ');
            return space > 0 ? city[..space] : city;
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

            // 핵심 4가지 — 방문 전에 가장 많이 확인하는 항목을 맨 위에 크게
            body.AppendLine("""<section class="keyfacts">""");
            body.AppendLine(KeyFact("일일 요금", r.Price, true));
            body.AppendLine(KeyFact("좌타석", r.LeftHanded));
            body.AppendLine(KeyFact("드라이버", r.DriverAllowed));
            body.AppendLine(KeyFact("주차", r.Parking));
            body.AppendLine("""</section>""");

            // 나머지 기본 정보
            var infoRows = new StringBuilder();
            void Row(string label, string? value)
            {
                if (IsUnknown(value)) return;
                var cell = label switch
                {
                    "전화" => $"""<a href="tel:{E(value!.Replace("-", ""))}">{E(value!)}</a>""",
                    _ => E(value!)
                };
                infoRows.AppendLine($"""<tr><th>{E(label)}</th><td>{cell}</td></tr>""");
            }
            Row("영업시간", r.Hours);
            Row("주소", r.Address);
            Row("전화", r.Phone);
            if (infoRows.Length > 0)
                body.AppendLine($"""<table class="info"><tbody>{infoRows}</tbody></table>""");

            if (r.Highlights.Count > 0)
            {
                body.AppendLine("""<section class="block good"><h2>이런 점이 좋아요</h2><ul>""");
                foreach (var h in r.Highlights) body.AppendLine($"<li>{E(h)}</li>");
                body.AppendLine("</ul></section>");
            }

            if (r.Cautions.Count > 0)
            {
                body.AppendLine("""<section class="block caution"><h2>미리 알아두세요</h2><ul>""");
                foreach (var c in r.Cautions) body.AppendLine($"<li>{E(c)}</li>");
                body.AppendLine("</ul></section>");
            }

            body.AppendLine($"""
                <section class="provenance">
                  <p class="asof">정보 기준 <strong>{E(r.UpdatedAt ?? "-")}</strong> · 요금과 운영 정책은 바뀔 수 있으니 방문 전 확인하세요.</p>
                """);
            if (r.SourceUrls.Count > 0)
            {
                body.AppendLine("""<details><summary>참고한 곳</summary><ul class="sources">""");
                foreach (var u in r.SourceUrls)
                    body.AppendLine($"""<li><a href="{E(u)}" target="_blank" rel="noopener nofollow">{E(u)}</a></li>""");
                body.AppendLine("</ul></details>");
            }
            body.AppendLine("""</section><p class="back"><a href="/">← 다른 연습장 보기</a></p>""");

            // 구조화 데이터 — 구글이 지역 업체로 이해하면 지도·리치결과에 노출될 수 있다
            var jsonLd = BuildJsonLd(r);
            var title = $"{r.Name} 요금·좌타석·주차 정보 — {SiteName}";
            var desc = BuildDetailDescription(r);
            return Page(title, desc, $"/range/{r.Slug}/", body.ToString(), jsonLd);
        }

        /// <summary>핵심 정보 카드. 값이 없으면 "미확인"으로 두되 시각적으로 가라앉힌다.</summary>
        private static string KeyFact(string label, string? value, bool lead = false)
        {
            var cls = lead ? "kf lead" : "kf";
            return IsUnknown(value)
                ? $"""<div class="{cls} unknown"><dt>{E(label)}</dt><dd>미확인</dd></div>"""
                : $"""<div class="{cls}"><dt>{E(label)}</dt><dd>{E(value!)}</dd></div>""";
        }

        /// <summary>
        /// 검색결과에 뜨는 설명문. 확인된 정보만 넣는다 —
        /// "미확인"으로 채우면 클릭할 이유가 없는 설명이 된다.
        /// </summary>
        private static string BuildDetailDescription(GolfRangeDto r)
        {
            var parts = new List<string> { $"{r.City ?? r.Region ?? ""} {r.Name}".Trim() };
            if (!IsUnknown(r.Price)) parts.Add($"요금 {r.Price}");
            if (!IsUnknown(r.Hours)) parts.Add($"영업시간 {r.Hours}");
            if (!IsUnknown(r.LeftHanded)) parts.Add($"좌타석 {r.LeftHanded}");
            if (!IsUnknown(r.DriverAllowed)) parts.Add($"드라이버 {r.DriverAllowed}");
            if (!IsUnknown(r.Parking)) parts.Add($"주차 {r.Parking}");
            if (parts.Count == 1 && !string.IsNullOrWhiteSpace(r.Summary)) parts.Add(r.Summary!);
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
