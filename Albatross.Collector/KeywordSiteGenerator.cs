using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Albatross.Collector
{
    /// <summary>
    /// 키워드 조사 결과를 "블로그 소재 고르는 화면"으로 만든다.
    ///
    /// 검색 유입용이 아니라 내가 보고 판단하는 작업 화면이라 색인을 막는다.
    /// 화면의 축은 3단계 트리다 — 대분류 탭 안에 중분류가 있고, 그 아래 소분류가 달린다.
    ///
    /// 왜 트리인가 — 평면 목록에서는 "주식"과 "sk하이닉스 주가전망"이 같은 높이에 놓여
    /// 무엇이 무엇의 갈래인지 안 보였다. 실제로 글을 쓸 대상은 소분류(L3)다.
    /// </summary>
    internal static class KeywordSiteGenerator
    {
        private const string SiteName = "Albatross Keyword";

        private sealed record Row(
            long Id, string Keyword, string Status,
            long? ParentId, string? Root, int Level,
            string Tier, int? Pc, int? Mobile, string? AdCompetition,
            int BlogTotal, int Analyzed, int ExactTitle,
            string? LatestPost, double? Score, string Grade,
            string MeasuredAt, List<Post> TopPosts)
        {
            public int Volume => (Pc ?? 0) + (Mobile ?? 0);
        }

        private sealed record Post(string Title, string Link, string BloggerName, string PostDate, string Snippet);

        public static async Task<int> GenerateFromDatabaseAsync(string databasePath, string siteRoot, CancellationToken ct)
        {
            var rows = await LoadAsync(databasePath, ct);
            var outputDir = Path.Combine(siteRoot, "public");
            Directory.CreateDirectory(outputDir);

            await File.WriteAllTextAsync(Path.Combine(outputDir, "index.html"), BuildIndex(rows), Encoding.UTF8, ct);
            // 내 작업 데이터라 검색에 노출될 이유가 없다
            await File.WriteAllTextAsync(Path.Combine(outputDir, "robots.txt"),
                "User-agent: *\nDisallow: /\n", Encoding.UTF8, ct);
            return 1;
        }

        private static async Task<List<Row>> LoadAsync(string databasePath, CancellationToken ct)
        {
            var list = new List<Row>();
            await using var conn = new SqliteConnection($"Data Source={databasePath}");
            await conn.OpenAsync(ct);

            var check = conn.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='KeywordMetrics';";
            if (Convert.ToInt32(await check.ExecuteScalarAsync(ct)) == 0) return list;

            // 키워드마다 가장 최근 측정값 하나만. 대분류(L1)는 측정하지 않으므로 LEFT JOIN 이다.
            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT k.Id, k.Keyword, k.Status,
                       k.ParentId, k.RootKeyword, COALESCE(k.TreeLevel,0),
                       COALESCE(m.VolumeTier,'E-미미'),
                       m.NaverPc, m.NaverMobile, m.NaverCompetition,
                       COALESCE(m.BlogTotalCount,0), COALESCE(m.Analyzed,0), COALESCE(m.ExactTitleMatches,0),
                       m.LatestPostDate, m.OpportunityScore, COALESCE(m.Grade,'-'),
                       COALESCE(m.MeasuredAt,''), COALESCE(m.TopPostsJson,'[]')
                FROM Keywords k
                LEFT JOIN KeywordMetrics m
                  ON m.Id = (SELECT Id FROM KeywordMetrics
                             WHERE KeywordId = k.Id ORDER BY MeasuredAt DESC LIMIT 1)
                ORDER BY COALESCE(m.OpportunityScore,0) DESC;
                """;

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                int? I(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
                string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                var posts = new List<Post>();
                try { posts = JsonSerializer.Deserialize<List<Post>>(r.GetString(17)) ?? new(); } catch { }

                list.Add(new Row(
                    r.GetInt64(0), r.GetString(1), r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetInt64(3), S(4), r.GetInt32(5),
                    r.GetString(6), I(7), I(8), S(9),
                    r.GetInt32(10), r.GetInt32(11), r.GetInt32(12),
                    S(13), r.IsDBNull(14) ? null : r.GetDouble(14), r.GetString(15),
                    r.GetString(16), posts));
            }
            return list;
        }

        private static string BuildIndex(List<Row> rows)
        {
            var body = new StringBuilder();
            var latest = rows.Where(r => r.MeasuredAt.Length > 0).Select(r => r.MeasuredAt).DefaultIfEmpty("").Max();
            var measured = DateTimeOffset.TryParse(latest, out var dt) ? dt.ToString("yyyy년 M월 d일 HH:mm") : "-";

            body.AppendLine($"""
                <header class="top">
                  <div class="brandrow">
                    <span class="brand">Albatross <em>Keyword</em></span>
                    <span class="stamp">{E(measured)} 기준</span>
                  </div>
                  <h1>이 주제로 쓰면 될 것 같습니다</h1>
                  <p class="lede">대분류 → 중분류 → 소분류 세 단계로 나눴습니다.
                     <strong>실제로 글을 쓸 대상은 소분류</strong>입니다. 중분류는 묶음 이름에 가깝습니다.</p>
                </header>
                """);

            if (rows.Count == 0)
            {
                body.AppendLine("""
                    <div class="empty">
                      <p>아직 조사 결과가 없습니다.</p>
                      <p class="hint">수집기에서 <code>--tree "주식" "금리"</code> 로 조사한 뒤
                         <code>--export-keywords</code> 를 실행하세요.</p>
                    </div>
                    """);
                return Page(body.ToString());
            }

            var leaves = rows.Where(r => r.Level == 3).ToList();
            var good = leaves.Count(r => r.Grade is "노려볼만함" or "해볼만함");
            body.AppendLine($"""
                <section class="stats">
                  <div class="stat"><b>{rows.Count(r => r.Level == 2)}</b><span>중분류</span></div>
                  <div class="stat"><b>{leaves.Count}</b><span>소분류 (글감)</span></div>
                  <div class="stat good"><b>{good}</b><span>쓸 만한 것</span></div>
                </section>
                """);

            // 대분류 탭 — 트리에 속하지 않은 예전 데이터는 마지막 "미분류" 탭으로 모은다
            var roots = rows.Where(r => r.Level >= 1 && r.Root != null)
                            .Select(r => r.Root!).Distinct().OrderBy(x => x).ToList();
            var orphans = rows.Where(r => r.Level == 0).ToList();

            body.AppendLine("""<nav class="tabs" id="tabs">""");
            for (var i = 0; i < roots.Count; i++)
            {
                var n = rows.Count(r => r.Root == roots[i] && r.Level == 3);
                body.AppendLine($"""<button class="tab{(i == 0 ? " on" : "")}" data-tab="{i}">{E(roots[i])} <span>{n}</span></button>""");
            }
            if (orphans.Count > 0)
                body.AppendLine($"""<button class="tab{(roots.Count == 0 ? " on" : "")}" data-tab="{roots.Count}">미분류 <span>{orphans.Count}</span></button>""");
            body.AppendLine("""</nav>""");

            body.AppendLine("""
                <section class="toolbar" id="filters">
                  <div class="fgroup"><span class="flabel">등급</span>
                    <button class="fbtn on" data-v="">전체</button>
                """);
            foreach (var g in new[] { "노려볼만함", "해볼만함", "보통", "어려움" }.Where(g => rows.Any(r => r.Grade == g)))
                body.AppendLine($"""<button class="fbtn" data-v="{E(g)}">{E(g)}</button>""");
            body.AppendLine("""
                  </div>
                  <label class="search"><input type="search" id="q" placeholder="키워드 찾기" autocomplete="off"></label>
                </section>
                <p class="count" id="count"></p>
                """);

            // 대분류별 패널 — 중분류 묶음 안에 소분류를 담는다
            for (var i = 0; i < roots.Count; i++)
            {
                body.AppendLine($"""<div class="tabpanel" data-tab="{i}"{(i == 0 ? "" : " hidden")}>""");
                var mids = rows.Where(r => r.Root == roots[i] && r.Level == 2)
                               .OrderByDescending(r => r.Volume).ToList();
                if (mids.Count == 0)
                    body.AppendLine("""<p class="empty">중분류가 없습니다.</p>""");

                foreach (var mid in mids)
                {
                    var kids = rows.Where(r => r.ParentId == mid.Id).OrderByDescending(r => r.Score ?? 0).ToList();
                    body.AppendLine($"""
                        <section class="branch">
                          <div class="branchhead">
                            <h2>{E(mid.Keyword)}</h2>
                            <div class="bmeta">
                              <span>월 {mid.Volume:N0}</span>
                              <span class="badge {GradeClass(mid.Grade)}">{E(mid.Grade)}</span>
                              <span class="kids">소분류 {kids.Count}</span>
                            </div>
                          </div>
                          <div class="list">
                        """);
                    foreach (var kid in kids) body.AppendLine(BuildRow(kid));
                    if (kids.Count == 0)
                        body.AppendLine("""<p class="nokid">소분류가 아직 없습니다.</p>""");
                    body.AppendLine("""</div></section>""");
                }
                body.AppendLine("""</div>""");
            }

            if (orphans.Count > 0)
            {
                body.AppendLine($"""<div class="tabpanel" data-tab="{roots.Count}"{(roots.Count == 0 ? "" : " hidden")}>""");
                body.AppendLine("""<p class="tierhint">트리로 나누기 전에 수집된 키워드입니다. 다시 조사하면 제자리를 찾아갑니다.</p>""");
                body.AppendLine("""<div class="list">""");
                foreach (var o in orphans.OrderByDescending(r => r.Score ?? 0)) body.AppendLine(BuildRow(o));
                body.AppendLine("""</div></div>""");
            }

            body.AppendLine("""<p class="noresult" id="noresult" hidden>조건에 맞는 키워드가 없습니다.</p>""");
            body.AppendLine(Script());
            return Page(body.ToString());
        }

        private static string GradeClass(string grade) => grade switch
        {
            "노려볼만함" => "g1",
            "해볼만함" => "g2",
            "보통" => "g3",
            "수요적음" => "g5",
            "-" => "g5",
            _ => "g4"
        };

        private static string BuildRow(Row r)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"""
                <article class="row" data-grade="{E(r.Grade)}" data-kw="{E(r.Keyword.ToLowerInvariant())}">
                  <div class="rowhead">
                    <div class="kw">
                      <span class="badge {GradeClass(r.Grade)}">{E(r.Grade)}</span>
                      <h3>{E(r.Keyword)}</h3>
                      {(r.Status != "후보" ? $"""<span class="status">{E(r.Status)}</span>""" : "")}
                    </div>
                    <div class="metrics">
                      <div class="m"><b>{(r.Pc is null ? "-" : r.Volume.ToString("N0"))}</b><span>월간 검색</span></div>
                      <div class="m"><b>{r.ExactTitle}/{r.Analyzed}</b><span>제목 일치</span></div>
                      <div class="m hi"><b>{(r.Score is { } s ? s.ToString("0.#") : "-")}</b><span>기회 점수</span></div>
                    </div>
                  </div>
                  <div class="detail">
                    <div class="submetrics">
                      <span>PC {r.Pc?.ToString("N0") ?? "-"}</span>
                      <span>모바일 {r.Mobile?.ToString("N0") ?? "-"}</span>
                      <span>광고 경쟁 {E(r.AdCompetition ?? "-")}</span>
                      <span>최근 글 {r.BlogTotal:N0}</span>
                      <span>최신 글 {E(r.LatestPost ?? "-")}</span>
                      <span>{E(r.Tier)}</span>
                    </div>
                """);

            if (r.TopPosts.Count > 0)
            {
                sb.AppendLine("""<h4>이 키워드로 검색되는 최근 글 (최신순)</h4><ol class="posts">""");
                foreach (var p in r.TopPosts)
                    sb.AppendLine($"""
                        <li>
                          <a href="{E(p.Link)}" target="_blank" rel="noopener">{E(p.Title)}</a>
                          <span class="meta">{E(p.BloggerName)} · {E(p.PostDate)}</span>
                        </li>
                        """);
                sb.AppendLine("""</ol>""");
            }

            sb.AppendLine("""</div></article>""");
            return sb.ToString();
        }

        private static string Script() => """
            <script>
            (function(){
              var grade='', q='';
              var rows=[].slice.call(document.querySelectorAll('.row'));
              var count=document.getElementById('count'), none=document.getElementById('noresult');
              function panel(){ return document.querySelector('.tabpanel:not([hidden])'); }
              function apply(){
                var p=panel(), n=0;
                rows.forEach(function(el){
                  var ok = p && p.contains(el)
                        && (!grade || el.dataset.grade===grade)
                        && (!q || el.dataset.kw.indexOf(q)>-1);
                  el.hidden=!ok; if(ok)n++;
                });
                // 소분류가 전부 걸러진 중분류는 통째로 감춘다
                [].forEach.call(document.querySelectorAll('.branch'),function(b){
                  var vis=[].slice.call(b.querySelectorAll('.row')).some(function(x){return !x.hidden;});
                  b.hidden=!vis;
                });
                count.textContent=n+'개';
                none.hidden=n>0;
              }
              document.getElementById('tabs').addEventListener('click',function(e){
                var b=e.target.closest('.tab'); if(!b)return;
                [].forEach.call(document.querySelectorAll('.tab'),function(x){x.classList.remove('on');});
                b.classList.add('on');
                [].forEach.call(document.querySelectorAll('.tabpanel'),function(p){
                  p.hidden = p.dataset.tab !== b.dataset.tab;
                });
                apply();
              });
              document.getElementById('filters').addEventListener('click',function(e){
                var b=e.target.closest('.fbtn'); if(!b)return;
                [].forEach.call(document.querySelectorAll('.fbtn'),function(x){x.classList.remove('on');});
                b.classList.add('on'); grade=b.dataset.v; apply();
              });
              document.getElementById('q').addEventListener('input',function(e){
                q=e.target.value.trim().toLowerCase(); apply();
              });
              document.addEventListener('click',function(e){
                var h=e.target.closest('.rowhead'); if(!h)return;
                h.parentElement.classList.toggle('open');
              });
              apply();
            })();
            </script>
            """;

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
              <p>대분류는 내가 정한 시드, 중분류는 검색광고 연관 키워드, 소분류는 자동완성에서 나옵니다.</p>
              <p>제목 일치 = 최근 글 10개 중 제목에 그 키워드를 그대로 쓴 글 수.
                 네이버가 보고하는 전체 건수는 검색어를 쪼개 느슨하게 세므로 경쟁 지표로 쓰지 않습니다.</p>
              <p>글 목록은 정확도순이 아니라 <strong>최신순</strong>입니다. 정확도순은 관련 없는 글이 섞여 나옵니다.</p>
            </footer>
            </body>
            </html>
            """;

        private static string E(string s) => WebUtility.HtmlEncode(s ?? string.Empty);
    }
}
