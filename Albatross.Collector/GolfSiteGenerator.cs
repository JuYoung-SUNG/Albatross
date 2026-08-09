using System.Net;
using System.Text;
using System.Text.Json;
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

        public static async Task<int> GenerateAsync(
            List<GolfRangeDto> ranges,
            List<GolfTournamentImporter.TournamentRow> tournaments,
            string outputDir,
            CancellationToken ct)
        {
            Directory.CreateDirectory(outputDir);
            var pages = 0;

            // 홈 — 대회 중심. 연습장 목록은 /ranges/ 로 내렸다.
            await Write(Path.Combine(outputDir, "index.html"), BuildHome(tournaments, ranges));
            await Write(Path.Combine(outputDir, "tier", "index.html"), BuildTierGuide(tournaments));
            await Write(Path.Combine(outputDir, "ranges", "index.html"), BuildRangeIndex(ranges));

            foreach (var t in tournaments.Where(t => t.Season >= DateTime.Now.Year - 1))
                await Write(Path.Combine(outputDir, "tournament", t.Slug, "index.html"), BuildTournamentDetail(t, tournaments));

            foreach (var r in ranges)
                await Write(Path.Combine(outputDir, "range", r.Slug, "index.html"), BuildDetail(r));

            await File.WriteAllTextAsync(Path.Combine(outputDir, "sitemap.xml"),
                BuildSitemap(ranges, tournaments), Encoding.UTF8, ct);
            await File.WriteAllTextAsync(Path.Combine(outputDir, "robots.txt"), BuildRobots(), Encoding.UTF8, ct);

            return pages;

            async Task Write(string path, string html)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, html, Encoding.UTF8, ct);
                pages++;
            }
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
                  <nav><a href="/">대회</a><a href="/tier/">등급 기준</a><a href="/ranges/">연습장</a></nav>
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

        // ── 홈: 대회 중심 ─────────────────────────────────────────────
        private static string BuildHome(List<GolfTournamentImporter.TournamentRow> all, List<GolfRangeDto> ranges)
        {
            var season = all.Count > 0 ? all.Max(t => t.Season) : DateTime.Now.Year;
            var year = all.Where(t => t.Season == season).OrderBy(t => t.StartDate).ToList();
            var regular = year.Where(t => t.TourType == "RE").ToList();
            var today = DateTime.Now.ToString("yyyy-MM-dd");

            var body = new StringBuilder();
            body.AppendLine($"""
                <section class="hero">
                  <p class="kicker">KLPGA {season} 시즌</p>
                  <h1>어느 대회가<br>어느 정도 급인가</h1>
                  <p class="lede">우승 상금만 봐서는 대회의 무게를 알기 어렵습니다.
                     메이저인지, 상금은 얼마인지, 몇 명이 나오는지를 정리하고
                     <strong>왜 그 등급인지 근거까지</strong> 함께 적었습니다.</p>
                </section>
                """);

            if (year.Count == 0)
            {
                body.AppendLine("""<p class="empty">대회 정보를 준비하고 있습니다.</p>""");
                return Page($"KLPGA 대회 등급과 일정 — {SiteName}",
                    "KLPGA 대회의 등급, 총상금, 개최 코스, 우승자를 정리했습니다.", "/", body.ToString());
            }

            // 요약
            var majors = year.Count(t => t.Tier == "메이저");
            var totalPrize = regular.Sum(t => t.PrizeMoney ?? 0);
            body.AppendLine($"""
                <section class="stats">
                  <div class="stat"><b>{regular.Count}</b><span>정규투어 대회</span></div>
                  <div class="stat"><b>{majors}</b><span>메이저</span></div>
                  <div class="stat"><b>{FormatMoney(totalPrize)}</b><span>정규투어 총상금</span></div>
                </section>
                """);

            // 다음 대회 — 방문자가 가장 먼저 궁금해하는 것
            var next = year.FirstOrDefault(t => string.CompareOrdinal(t.StartDate, today) >= 0 && t.TourType == "RE");
            if (next != null)
            {
                body.AppendLine($"""
                    <section class="next">
                      <p class="label">다음 정규투어</p>
                      <h2><a href="/tournament/{E(next.Slug)}/">{E(next.Title)}</a></h2>
                      <p class="when">{E(FormatRange(next.StartDate, next.EndDate))}{(next.Course is null ? "" : " · " + E(next.Course))}</p>
                      <span class="badge {TierClass(next.Tier)}">{E(next.Tier)}</span>
                    </section>
                    """);
            }

            body.AppendLine($"""
                <section class="block">
                  <div class="blockhead">
                    <h2>{season} 정규투어 일정</h2>
                    <a class="more" href="/tier/">등급은 어떻게 나눴나 →</a>
                  </div>
                </section>
                """);

            body.AppendLine("""<section class="toolbar" id="filters">""");
            body.AppendLine("""<div class="fgroup"><span class="flabel">등급</span><button class="fbtn on" data-f="tier" data-v="">전체</button>""");
            foreach (var t in new[] { "메이저", "특급", "정규투어" }.Where(x => year.Any(y => y.Tier == x)))
                body.AppendLine($"""<button class="fbtn" data-f="tier" data-v="{E(t)}">{E(t)}</button>""");
            body.AppendLine("""</div>""");
            body.AppendLine("""<label class="search"><input type="search" id="q" placeholder="대회 이름이나 골프장으로 찾기" autocomplete="off"></label>""");
            body.AppendLine("""</section><p class="count" id="count"></p><div class="grid" id="grid">""");

            foreach (var t in regular) body.AppendLine(BuildTournamentCard(t, today));

            body.AppendLine("""</div><p class="noresult" id="noresult" hidden>조건에 맞는 대회가 없습니다.</p>""");

            // 다른 투어
            var others = year.Where(t => t.TourType != "RE").GroupBy(t => t.Tier).ToList();
            if (others.Count > 0)
            {
                body.AppendLine("""<section class="block"><h2>그 밖의 투어</h2><div class="tourlist">""");
                foreach (var g in others.OrderBy(g => g.Key))
                    body.AppendLine($"""<div class="tourrow"><span class="badge {TierClass(g.Key)}">{E(g.Key)}</span><span>{g.Count()}개 대회</span></div>""");
                body.AppendLine("""</div></section>""");
            }

            if (ranges.Count > 0)
            {
                body.AppendLine($"""
                    <section class="block sidelink">
                      <h2>연습장도 정리하고 있습니다</h2>
                      <p>수도권 파3·야외 연습장 {ranges.Count}곳의 요금과 좌타석 정보입니다.</p>
                      <a class="more" href="/ranges/">연습장 보기 →</a>
                    </section>
                    """);
            }

            body.AppendLine(FilterScript("card", "tier"));

            var desc = $"KLPGA {season} 시즌 정규투어 {regular.Count}개 대회의 등급, 총상금, 개최 코스, 우승자를 정리했습니다. 메이저 {majors}개 포함.";
            return Page($"KLPGA {season} 대회 등급·일정·총상금 — {SiteName}", desc, "/", body.ToString());
        }

        private static string BuildTournamentCard(GolfTournamentImporter.TournamentRow t, string today)
        {
            var search = $"{t.Title} {t.EngTitle} {t.Course}".ToLowerInvariant();
            var done = string.CompareOrdinal(t.EndDate ?? t.StartDate, today) < 0;
            var sb = new StringBuilder();
            sb.AppendLine($"""
                <article class="card" data-tier="{E(t.Tier)}" data-search="{E(search)}">
                  <a class="cardlink" href="/tournament/{E(t.Slug)}/">
                    <span class="badge {TierClass(t.Tier)}">{E(t.Tier)}</span>
                    <h3>{E(t.Title)}</h3>
                    <p class="when">{E(FormatRange(t.StartDate, t.EndDate))}</p>
                """);
            if (t.PrizeMoney is { } p)
                sb.AppendLine($"""<p class="prize">총상금 {E(FormatMoney(p))}</p>""");
            if (t.Course != null)
                sb.AppendLine($"""<p class="course">{E(t.Course)}</p>""");
            if (done && t.WinnerName != null)
                sb.AppendLine($"""<p class="winner">우승 <strong>{E(t.WinnerName)}</strong></p>""");
            sb.AppendLine("""</a></article>""");
            return sb.ToString();
        }

        // ── 대회 상세 ─────────────────────────────────────────────────
        private static string BuildTournamentDetail(
            GolfTournamentImporter.TournamentRow t, List<GolfTournamentImporter.TournamentRow> all)
        {
            var body = new StringBuilder();
            body.AppendLine($"""
                <nav class="crumb"><a href="/">대회 목록</a> › <span>{E(t.Title)}</span></nav>
                <span class="badge {TierClass(t.Tier)}">{E(t.Tier)}</span>
                <h1>{E(t.Title)}</h1>
                """);
            if (t.EngTitle != null) body.AppendLine($"""<p class="engtitle">{E(t.EngTitle)}</p>""");

            if (t.TierReason != null)
                body.AppendLine($"""<div class="tierbox"><p class="label">이 대회의 급</p><p>{E(t.TierReason)}</p></div>""");

            body.AppendLine("""<section class="keyfacts">""");
            body.AppendLine(KeyFact("총상금", t.PrizeMoney is { } p ? FormatMoney(p) : null, true));
            body.AppendLine(KeyFact("일정", FormatRange(t.StartDate, t.EndDate)));
            body.AppendLine(KeyFact("개최 코스", t.Course));
            body.AppendLine(KeyFact("출전 인원", t.EntryCount is { } e ? $"{e}명" : null));
            body.AppendLine("""</section>""");

            var rows = new StringBuilder();
            void Row(string label, string? value)
            {
                if (!IsUnknown(value)) rows.AppendLine($"""<tr><th>{E(label)}</th><td>{E(value!)}</td></tr>""");
            }
            Row("경기 방식", t.Rounds is { } r ? $"{r}라운드 스트로크 플레이" : null);
            Row("코스 파", t.Par is { } par ? $"파 {par}" : null);
            Row("직전 우승자", t.DefendingName);
            Row("이번 우승자", t.WinnerName);
            if (rows.Length > 0) body.AppendLine($"""<table class="info"><tbody>{rows}</tbody></table>""");

            // 같은 대회의 지난 시즌 — 시즌이 다른 것만, 그리고 확실히 같은 대회일 때만
            var history = all.Where(x => x.Season != t.Season
                                      && x.WinnerName != null
                                      && IsSameEvent(x.Title, t.Title))
                             .OrderByDescending(x => x.Season).Take(6).ToList();
            if (history.Count > 0)
            {
                body.AppendLine("""<section class="block"><h2>역대 우승자</h2><table class="info"><tbody>""");
                foreach (var h in history)
                    body.AppendLine($"""<tr><th>{h.Season}</th><td>{E(h.WinnerName!)}</td></tr>""");
                body.AppendLine("""</tbody></table></section>""");
            }

            var same = all.Where(x => x.Season == t.Season && x.Tier == t.Tier && x.Slug != t.Slug)
                          .OrderBy(x => x.StartDate).Take(4).ToList();
            if (same.Count > 0)
            {
                body.AppendLine($"""<section class="block"><h2>같은 등급의 다른 대회</h2><ul class="linklist">""");
                foreach (var s in same)
                    body.AppendLine($"""<li><a href="/tournament/{E(s.Slug)}/">{E(s.Title)}</a></li>""");
                body.AppendLine("""</ul></section>""");
            }

            body.AppendLine("""
                <section class="provenance">
                  <p class="asof">일정·총상금·우승자는 KLPGA 공식 자료 기준입니다. 등급은 이 사이트가 매긴 것으로,
                     협회의 공식 구분이 아닙니다. <a href="/tier/">기준 보기</a></p>
                </section>
                <p class="back"><a href="/">← 다른 대회 보기</a></p>
                """);

            var descParts = new List<string> { $"{t.Season} {t.Title}" };
            if (t.PrizeMoney is { } pm) descParts.Add($"총상금 {FormatMoney(pm)}");
            descParts.Add(FormatRange(t.StartDate, t.EndDate));
            if (t.Course != null) descParts.Add(t.Course);
            if (t.WinnerName != null) descParts.Add($"우승 {t.WinnerName}");
            var desc = string.Join(" · ", descParts);

            return Page($"{t.Title} 총상금·일정·우승자 — {SiteName}",
                desc.Length > 155 ? desc[..152] + "..." : desc,
                $"/tournament/{t.Slug}/", body.ToString(), BuildTournamentJsonLd(t));
        }

        // ── 등급 안내 ─────────────────────────────────────────────────
        private static string BuildTierGuide(List<GolfTournamentImporter.TournamentRow> all)
        {
            var season = all.Count > 0 ? all.Max(t => t.Season) : DateTime.Now.Year;
            var body = new StringBuilder();
            body.AppendLine($"""
                <nav class="crumb"><a href="/">대회 목록</a> › <span>등급 기준</span></nav>
                <h1>대회 등급은 이렇게 나눴습니다</h1>
                <p class="lede">협회는 대회에 등급을 매겨 공개하지 않습니다.
                   그래서 <strong>메이저 지정 여부와 총상금</strong>을 기준으로 직접 나눴고,
                   각 대회 페이지에 왜 그 등급인지 적어뒀습니다.</p>

                <section class="tiers">
                  <div class="tiercard t-major">
                    <span class="badge tmajor">메이저</span>
                    <p>협회가 메이저로 지정한 대회입니다. {season}시즌은 <strong>한국여자오픈, KLPGA 챔피언십,
                       KB금융 챔피언십, 하이트진로 챔피언십</strong> 네 개입니다.
                       우승하면 장기 시드가 따라오고, 커리어에서 차지하는 무게가 일반 대회와 다릅니다.</p>
                  </div>
                  <div class="tiercard t-premier">
                    <span class="badge tpremier">특급</span>
                    <p>메이저는 아니지만 <strong>총상금 12억원 이상</strong>인 대회입니다.
                       상금이 크면 상금랭킹·대상 포인트 비중도 커서 출전 경쟁이 치열합니다.</p>
                  </div>
                  <div class="tiercard t-regular">
                    <span class="badge tregular">정규투어</span>
                    <p>그 밖의 정규투어 대회입니다. {season}시즌부터는 모든 정규투어 대회의
                       총상금이 10억원을 넘습니다.</p>
                  </div>
                  <div class="tiercard t-second">
                    <span class="badge tsecond">2부 · 3부</span>
                    <p><strong>드림투어(2부)</strong>는 정규투어로 올라가는 등용문입니다.
                       상금랭킹 상위 선수가 다음 시즌 정규투어 시드를 받습니다.
                       <strong>점프투어(3부)</strong>는 그 아래 단계입니다.</p>
                  </div>
                </section>

                <div class="note">
                  <p><strong>주의</strong> — 메이저 지정은 해마다 바뀝니다.
                     2024년 한화 클래식이 없어지고 2025년에는 새 메이저를 찾지 못해 3개까지 줄었다가,
                     {season}시즌에 다시 4개가 됐습니다. 시즌이 바뀌면 이 목록도 갱신해야 합니다.</p>
                </div>
                <p class="back"><a href="/">← 대회 목록으로</a></p>
                """);

            return Page($"KLPGA 대회 등급 기준 — {SiteName}",
                "KLPGA 대회를 메이저·특급·정규투어·2부/3부로 나눈 기준과 근거를 설명합니다.",
                "/tier/", body.ToString());
        }

        private static string BuildTournamentJsonLd(GolfTournamentImporter.TournamentRow t)
        {
            static string J(string? s) => JsonSerializer.Serialize(s ?? "");
            var sb = new StringBuilder();
            sb.AppendLine("""<script type="application/ld+json">""");
            sb.AppendLine("{");
            sb.AppendLine(""" "@context": "https://schema.org",""");
            sb.AppendLine(""" "@type": "SportsEvent",""");
            sb.AppendLine($""" "name": {J(t.Title)},""");
            sb.AppendLine($""" "startDate": {J(t.StartDate)},""");
            if (t.EndDate != null) sb.AppendLine($""" "endDate": {J(t.EndDate)},""");
            sb.AppendLine(""" "sport": "Golf",""");
            if (t.Course != null)
            {
                sb.AppendLine(""" "location": {""");
                sb.AppendLine("""   "@type": "Place",""");
                sb.AppendLine($"""   "name": {J(t.Course)},""");
                sb.AppendLine("""   "address": { "@type": "PostalAddress", "addressCountry": "KR" }""");
                sb.AppendLine(" },");
            }
            sb.AppendLine($""" "url": {J($"{SiteUrl}/tournament/{t.Slug}/")}""");
            sb.AppendLine("}");
            sb.AppendLine("</script>");
            return sb.ToString();
        }

        private static string TierClass(string tier) => tier switch
        {
            "메이저" => "tmajor",
            "특급" => "tpremier",
            "정규투어" => "tregular",
            "2부 드림투어" or "3부 점프투어" => "tsecond",
            _ => "tother"
        };

        private static string FormatMoney(long won) =>
            won >= 100_000_000 ? $"{won / 100_000_000.0:0.#}억원" : $"{won / 10000:N0}만원";

        private static string FormatRange(string start, string? end)
        {
            if (string.IsNullOrWhiteSpace(end) || end == start) return start;
            // 같은 해·같은 달이면 뒤쪽을 줄여 쓴다: 2026-06-11 ~ 14
            return start[..7] == end[..7] ? $"{start} ~ {end[8..]}" : $"{start} ~ {end}";
        }

        /// <summary>
        /// 대회 이름에서 해마다 바뀌는 부분(회차, 연도, 공백·기호)을 걷어낸다.
        /// 스폰서는 앞에 붙고 대회 고유명은 뒤에 오므로, 비교는 뒤쪽 문자열로 한다.
        /// </summary>
        private static string NormalizeTitle(string title)
        {
            var s = System.Text.RegularExpressions.Regex.Replace(title, @"제?\s*\d+\s*회", " ");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\b20\d{2}\b", " ");
            return System.Text.RegularExpressions.Regex.Replace(s, @"[\s·ㆍ\-—_,.]+", "");
        }

        /// <summary>
        /// 두 대회가 "해마다 열리는 같은 대회"인지 판단한다. 회차·연도만 뗀 뒤 완전히 같아야 한다.
        ///
        /// 접미사 일치로 느슨하게 잡아봤다가 두 번 틀렸다.
        /// "오픈"으로 끝나는 대회가 전부 한 대회로 묶였고, 기준을 올려도
        /// "레이디스 챔피언십"으로 끝나는 서로 다른 대회들이 여전히 묶였다.
        /// 2025·2026 정규투어로 확인한 결과 완전일치는 29개 중 18개를 잡고 오탐이 없다.
        /// 스폰서가 바뀐 대회는 놓치지만, 남의 우승 기록을 잘못 붙이는 것보다 낫다.
        /// </summary>
        private static bool IsSameEvent(string titleA, string titleB)
        {
            var a = NormalizeTitle(titleA);
            var b = NormalizeTitle(titleB);
            return a.Length > 0 && a == b;
        }

        /// <summary>목록 필터 스크립트. 카드 클래스와 필터 키만 바꿔 재사용한다.</summary>
        private static string FilterScript(string itemClass, string key) => $$"""
            <script>
            (function(){
              var sel='', q='';
              var items=[].slice.call(document.querySelectorAll('.{{itemClass}}'));
              var count=document.getElementById('count'), none=document.getElementById('noresult');
              function apply(){
                var n=0;
                items.forEach(function(el){
                  var ok=(!sel||el.dataset.{{key}}===sel)&&(!q||el.dataset.search.indexOf(q)>-1);
                  el.hidden=!ok; if(ok)n++;
                });
                if(count)count.textContent=n+'개';
                if(none)none.hidden=n>0;
              }
              var f=document.getElementById('filters');
              if(f)f.addEventListener('click',function(e){
                var b=e.target.closest('.fbtn'); if(!b)return;
                sel=b.dataset.v;
                [].forEach.call(f.querySelectorAll('.fbtn'),function(x){x.classList.remove('on');});
                b.classList.add('on'); apply();
              });
              var qi=document.getElementById('q');
              if(qi)qi.addEventListener('input',function(e){q=e.target.value.trim().toLowerCase();apply();});
              apply();
            })();
            </script>
            """;

        // ── 연습장 목록 (/ranges/) ────────────────────────────────────
        private static string BuildRangeIndex(List<GolfRangeDto> ranges)
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
                    "/ranges/", body.ToString());
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
            return Page($"수도권 골프 연습장 {sorted.Count}곳 요금·좌타석 정보 — {SiteName}", desc, "/ranges/", body.ToString());
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
        private static string BuildSitemap(
            List<GolfRangeDto> ranges, List<GolfTournamentImporter.TournamentRow> tournaments)
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var sb = new StringBuilder();
            sb.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
            sb.AppendLine("""<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">""");

            Add("/", today, "weekly", "1.0");
            Add("/tier/", today, "monthly", "0.9");
            Add("/ranges/", today, "weekly", "0.7");

            // 시즌 중인 대회는 결과가 계속 바뀌므로 자주 확인해달라고 알린다
            foreach (var t in tournaments.Where(t => t.Season >= DateTime.Now.Year - 1))
                Add($"/tournament/{t.Slug}/", today,
                    t.Season >= DateTime.Now.Year ? "weekly" : "yearly",
                    t.Tier == "메이저" ? "0.9" : "0.8");

            foreach (var r in ranges)
                Add($"/range/{r.Slug}/", string.IsNullOrWhiteSpace(r.UpdatedAt) ? today : r.UpdatedAt!, "monthly", "0.6");

            sb.AppendLine("</urlset>");
            return sb.ToString();

            void Add(string path, string lastmod, string freq, string priority) =>
                sb.AppendLine($"  <url><loc>{SiteUrl}{path}</loc><lastmod>{lastmod}</lastmod><changefreq>{freq}</changefreq><priority>{priority}</priority></url>");
        }

        private static string BuildRobots() => $"""
            User-agent: *
            Allow: /

            Sitemap: {SiteUrl}/sitemap.xml
            """;

        private static string E(string s) => WebUtility.HtmlEncode(s ?? string.Empty);
    }
}
