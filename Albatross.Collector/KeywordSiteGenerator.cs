using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Albatross.Collector
{
    /// <summary>
    /// KeywordOpportunities 테이블로 "블로그 소재 대시보드"를 정적 HTML로 만든다.
    ///
    /// 골프 사이트와 달리 이건 검색 유입용이 아니라 내가 보는 작업 화면이다.
    /// 그래서 색인은 막고(robots noindex), 대신 한눈에 훑고 판단하기 좋게 만든다.
    /// </summary>
    internal static class KeywordSiteGenerator
    {
        private const string SiteName = "Albatross Keyword";

        private sealed record Opportunity(
            string Keyword, string Category, string? Gist,
            int SearchPc, int SearchMobile, string Competition,
            int BlogTotal, double Score, string Grade,
            List<TopPost> TopPosts, List<string> SampleTitles, string AnalyzedAt)
        {
            public int SearchTotal => SearchPc + SearchMobile;
        }

        private sealed record TopPost(string Title, string Link, string BloggerName, string PostDate, string Snippet);

        public static async Task<int> GenerateFromDatabaseAsync(string databasePath, string siteRoot, CancellationToken ct)
        {
            var items = await LoadAsync(databasePath, ct);
            var outputDir = Path.Combine(siteRoot, "public");
            Directory.CreateDirectory(outputDir);

            await File.WriteAllTextAsync(Path.Combine(outputDir, "index.html"), BuildIndex(items), Encoding.UTF8, ct);
            // 이 화면은 검색에 노출될 이유가 없다 (내 작업용 데이터)
            await File.WriteAllTextAsync(Path.Combine(outputDir, "robots.txt"),
                "User-agent: *\nDisallow: /\n", Encoding.UTF8, ct);

            return 1;
        }

        private static async Task<List<Opportunity>> LoadAsync(string databasePath, CancellationToken ct)
        {
            var list = new List<Opportunity>();
            await using var conn = new SqliteConnection($"Data Source={databasePath}");
            await conn.OpenAsync(ct);

            var check = conn.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='KeywordOpportunities';";
            if (Convert.ToInt32(await check.ExecuteScalarAsync(ct)) == 0) return list;

            // 가장 최근에 분석한 회차만 보여준다 (과거 회차는 DB에 남아 있다)
            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Keyword, Category, Gist, MonthlySearchPc, MonthlySearchMobile, Competition,
                       BlogTotalCount, OpportunityScore, Grade, TopPostsJson, SampleTitlesJson, AnalyzedAt
                FROM KeywordOpportunities
                WHERE date(AnalyzedAt) = (SELECT MAX(date(AnalyzedAt)) FROM KeywordOpportunities)
                ORDER BY OpportunityScore DESC;
                """;

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                list.Add(new Opportunity(
                    r.GetString(0),
                    r.IsDBNull(1) ? "미분류" : r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetString(2),
                    r.GetInt32(3), r.GetInt32(4),
                    r.IsDBNull(5) ? "미상" : r.GetString(5),
                    r.GetInt32(6), r.GetDouble(7),
                    r.IsDBNull(8) ? "보통" : r.GetString(8),
                    Parse<TopPost>(r.GetString(9)),
                    Parse<string>(r.GetString(10)),
                    r.GetString(11)));
            }
            return list;

            static List<T> Parse<T>(string json)
            {
                try { return JsonSerializer.Deserialize<List<T>>(json) ?? new(); }
                catch { return new(); }
            }
        }

        private static string BuildIndex(List<Opportunity> items)
        {
            var body = new StringBuilder();
            var analyzed = items.Count > 0 && DateTimeOffset.TryParse(items[0].AnalyzedAt, out var dt)
                ? dt.ToString("yyyy년 M월 d일 HH:mm")
                : "-";

            body.AppendLine($"""
                <header class="top">
                  <div class="brandrow">
                    <span class="brand">Albatross <em>Keyword</em></span>
                    <span class="stamp">{E(analyzed)} 기준</span>
                  </div>
                  <h1>이 주제로 쓰면 될 것 같습니다</h1>
                  <p class="lede">뉴스에서 뽑은 키워드를 네이버 월간 검색수와 블로그 문서 수로 걸렀습니다.
                     <strong>찾는 사람은 많은데 쓴 글은 적은 순서</strong>입니다.</p>
                </header>
                """);

            if (items.Count == 0)
            {
                body.AppendLine("""
                    <div class="empty">
                      <p>아직 분석 결과가 없습니다.</p>
                      <p class="hint">수집기에서 <code>--extract-keywords</code> 로 키워드를 뽑은 뒤
                         <code>--analyze-keywords</code> 를 실행하세요.</p>
                    </div>
                    """);
                return Page(body.ToString());
            }

            // 요약 지표
            var pick = items.Count(i => i.Grade is "노려볼만함" or "해볼만함");
            body.AppendLine($"""
                <section class="stats">
                  <div class="stat"><b>{items.Count}</b><span>분석한 키워드</span></div>
                  <div class="stat good"><b>{pick}</b><span>쓸 만한 것</span></div>
                  <div class="stat"><b>{items.Max(i => i.SearchTotal):N0}</b><span>최고 월간 검색</span></div>
                </section>
                """);

            // 필터
            var grades = new[] { "노려볼만함", "해볼만함", "보통", "어려움" };
            var cats = items.Select(i => i.Category).Distinct().OrderBy(x => x).ToList();
            body.AppendLine("""<section class="toolbar" id="filters">""");
            body.AppendLine("""<div class="fgroup"><span class="flabel">등급</span><button class="fbtn on" data-f="grade" data-v="">전체</button>""");
            foreach (var g in grades.Where(g => items.Any(i => i.Grade == g)))
                body.AppendLine($"""<button class="fbtn" data-f="grade" data-v="{E(g)}">{E(g)}</button>""");
            body.AppendLine("""</div>""");
            body.AppendLine("""<div class="fgroup"><span class="flabel">분야</span><button class="fbtn on" data-f="cat" data-v="">전체</button>""");
            foreach (var c in cats)
                body.AppendLine($"""<button class="fbtn" data-f="cat" data-v="{E(c)}">{E(c)}</button>""");
            body.AppendLine("""</div>""");
            body.AppendLine("""
                <label class="search"><input type="search" id="q" placeholder="키워드 찾기" autocomplete="off"></label>
                </section>
                <p class="count" id="count"></p>
                <div class="list" id="list">
                """);

            foreach (var o in items) body.AppendLine(BuildRow(o));

            body.AppendLine("""
                </div>
                <p class="noresult" id="noresult" hidden>조건에 맞는 키워드가 없습니다.</p>
                <script>
                (function(){
                  var sel={grade:'',cat:''}, q='';
                  var rows=[].slice.call(document.querySelectorAll('.row'));
                  var count=document.getElementById('count'), none=document.getElementById('noresult');
                  function apply(){
                    var n=0;
                    rows.forEach(function(el){
                      var ok=(!sel.grade||el.dataset.grade===sel.grade)
                          &&(!sel.cat||el.dataset.cat===sel.cat)
                          &&(!q||el.dataset.kw.indexOf(q)>-1);
                      el.hidden=!ok; if(ok)n++;
                    });
                    count.textContent=n+'개';
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
                  // 행을 눌러 상위 글 펼치기
                  document.getElementById('list').addEventListener('click',function(e){
                    var h=e.target.closest('.rowhead'); if(!h)return;
                    var row=h.parentElement;
                    row.classList.toggle('open');
                  });
                  apply();
                })();
                </script>
                """);

            return Page(body.ToString());
        }

        private static string BuildRow(Opportunity o)
        {
            var gradeCls = o.Grade switch
            {
                "노려볼만함" => "g1",
                "해볼만함" => "g2",
                "보통" => "g3",
                _ => "g4"
            };

            var sb = new StringBuilder();
            sb.AppendLine($"""
                <article class="row" data-grade="{E(o.Grade)}" data-cat="{E(o.Category)}" data-kw="{E(o.Keyword.ToLowerInvariant())}">
                  <div class="rowhead">
                    <div class="kw">
                      <span class="badge {gradeCls}">{E(o.Grade)}</span>
                      <h2>{E(o.Keyword)}</h2>
                      <span class="cat">{E(o.Category)}</span>
                    </div>
                    <div class="metrics">
                      <div class="m"><b>{o.SearchTotal:N0}</b><span>월간 검색</span></div>
                      <div class="m"><b>{o.BlogTotal:N0}</b><span>블로그 글</span></div>
                      <div class="m hi"><b>{o.Score:0.##}</b><span>기회 점수</span></div>
                    </div>
                  </div>
                """);

            sb.AppendLine("""<div class="detail">""");

            if (!string.IsNullOrWhiteSpace(o.Gist))
                sb.AppendLine($"""<p class="gist">{E(o.Gist!)}</p>""");

            sb.AppendLine($"""
                <div class="submetrics">
                  <span>PC {o.SearchPc:N0}</span><span>모바일 {o.SearchMobile:N0}</span>
                  <span>광고 경쟁 {E(o.Competition)}</span>
                </div>
                """);

            if (o.TopPosts.Count > 0)
            {
                sb.AppendLine("""<h3>지금 상위에 있는 글</h3><ol class="posts">""");
                foreach (var p in o.TopPosts)
                {
                    sb.AppendLine($"""
                        <li>
                          <a href="{E(p.Link)}" target="_blank" rel="noopener">{E(p.Title)}</a>
                          <span class="meta">{E(p.BloggerName)} · {E(p.PostDate)}</span>
                        </li>
                        """);
                }
                sb.AppendLine("""</ol>""");
            }

            if (o.SampleTitles.Count > 0)
            {
                sb.AppendLine("""<h3>이 키워드가 나온 뉴스</h3><ul class="news">""");
                foreach (var t in o.SampleTitles.Take(5)) sb.AppendLine($"<li>{E(t)}</li>");
                sb.AppendLine("""</ul>""");
            }

            sb.AppendLine("""</div></article>""");
            return sb.ToString();
        }

        private static string Page(string body) => $"""
            <!doctype html>
            <html lang="ko">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="robots" content="noindex, nofollow">
            <title>블로그 소재 — {SiteName}</title>
            <link rel="stylesheet" href="/css/site.css">
            </head>
            <body>
            <main>
            {body}
            </main>
            <footer>
              <p>월간 검색수는 네이버 검색광고 키워드도구, 블로그 글 수와 상위 글은 네이버 블로그 검색 API 기준입니다.</p>
              <p>기회 점수 = 월간 검색수 ÷ 블로그 글 수. 클수록 수요 대비 쓴 글이 적다는 뜻입니다.</p>
            </footer>
            </body>
            </html>
            """;

        private static string E(string s) => WebUtility.HtmlEncode(s ?? string.Empty);
    }
}
