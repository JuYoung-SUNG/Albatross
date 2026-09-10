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
    /// 화면의 축은 검색량 구간(A-대형 / B-중형 / C-롱테일) 탭이다.
    /// 구간마다 노려야 할 방식이 달라서 섞어놓으면 판단이 흐려진다.
    /// </summary>
    internal static class KeywordSiteGenerator
    {
        private const string SiteName = "Albatross Keyword";

        private sealed record Row(
            long Id, string Keyword, string Status, string Tier,
            int? Pc, int? Mobile, string? AdCompetition,
            int BlogTotal, int Analyzed, int ExactTitle, int ExactAny,
            string? LatestPost, double? Score, string Grade,
            string MeasuredAt, List<Post> TopPosts)
        {
            public int Volume => (Pc ?? 0) + (Mobile ?? 0);
            public bool HasVolume => Pc is not null;
        }

        private sealed record Post(string Title, string Link, string BloggerName, string PostDate, string Snippet);

        private static readonly string[] Tiers = { "A-대형", "B-중형", "C-롱테일", "D-미미" };

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

            // 키워드마다 가장 최근 측정값 하나만
            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT k.Id, k.Keyword, k.Status,
                       COALESCE(m.VolumeTier,'D-미미'),
                       m.NaverPc, m.NaverMobile, m.NaverCompetition,
                       m.BlogTotalCount, m.Analyzed, m.ExactTitleMatches, m.ExactAnyMatches,
                       m.LatestPostDate, m.OpportunityScore, m.Grade, m.MeasuredAt, m.TopPostsJson
                FROM Keywords k
                JOIN KeywordMetrics m
                  ON m.Id = (SELECT Id FROM KeywordMetrics
                             WHERE KeywordId = k.Id ORDER BY MeasuredAt DESC LIMIT 1)
                ORDER BY m.OpportunityScore DESC;
                """;

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                int? I(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
                string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                var posts = new List<Post>();
                try { posts = JsonSerializer.Deserialize<List<Post>>(r.GetString(15)) ?? new(); } catch { }

                list.Add(new Row(
                    r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    I(4), I(5), S(6),
                    r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.GetInt32(10),
                    S(11), r.IsDBNull(12) ? null : r.GetDouble(12), r.GetString(13),
                    r.GetString(14), posts));
            }
            return list;
        }

        private static string BuildIndex(List<Row> rows)
        {
            var body = new StringBuilder();
            var measured = rows.Count > 0 && DateTimeOffset.TryParse(rows[0].MeasuredAt, out var dt)
                ? dt.ToString("yyyy년 M월 d일 HH:mm") : "-";

            body.AppendLine($"""
                <header class="top">
                  <div class="brandrow">
                    <span class="brand">Albatross <em>Keyword</em></span>
                    <span class="stamp">{E(measured)} 기준</span>
                  </div>
                  <h1>이 주제로 쓰면 될 것 같습니다</h1>
                  <p class="lede">네이버 월간 검색수와 블로그 경쟁을 재서 정리했습니다.
                     <strong>검색량 구간마다 노려야 할 방식이 다르므로</strong> 탭으로 나눴습니다.</p>
                </header>
                """);

            if (rows.Count == 0)
            {
                body.AppendLine("""
                    <div class="empty">
                      <p>아직 조사 결과가 없습니다.</p>
                      <p class="hint">수집기에서 <code>--keyword "주식" "금리"</code> 로 조사한 뒤
                         <code>--export-keywords</code> 를 실행하세요.</p>
                    </div>
                    """);
                return Page(body.ToString());
            }

            var good = rows.Count(r => r.Grade is "노려볼만함" or "해볼만함");
            body.AppendLine($"""
                <section class="stats">
                  <div class="stat"><b>{rows.Count}</b><span>조사한 키워드</span></div>
                  <div class="stat good"><b>{good}</b><span>쓸 만한 것</span></div>
                  <div class="stat"><b>{rows.Max(r => r.Volume):N0}</b><span>최고 월간 검색</span></div>
                </section>
                """);

            // 구간 탭
            var present = Tiers.Where(t => rows.Any(r => r.Tier == t)).ToList();
            body.AppendLine("""<nav class="tabs" id="tabs">""");
            for (var i = 0; i < present.Count; i++)
            {
                var n = rows.Count(r => r.Tier == present[i]);
                body.AppendLine($"""<button class="tab{(i == 0 ? " on" : "")}" data-tab="{i}">{E(present[i])} <span>{n}</span></button>""");
            }
            body.AppendLine("""</nav>""");

            // 등급 필터 + 검색
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

            for (var i = 0; i < present.Count; i++)
            {
                body.AppendLine($"""<div class="tabpanel" data-tab="{i}"{(i == 0 ? "" : " hidden")}>""");
                body.AppendLine($"""<p class="tierhint">{E(TierHint(present[i]))}</p>""");
                body.AppendLine("""<div class="list">""");
                foreach (var row in rows.Where(r => r.Tier == present[i]).OrderByDescending(r => r.Score ?? 0))
                    body.AppendLine(BuildRow(row));
                body.AppendLine("""</div></div>""");
            }

            body.AppendLine("""<p class="noresult" id="noresult" hidden>조건에 맞는 키워드가 없습니다.</p>""");
            body.AppendLine(Script());
            return Page(body.ToString());
        }

        /// <summary>구간마다 어떻게 접근해야 하는지 한 줄로 알려준다.</summary>
        private static string TierHint(string tier) => tier switch
        {
            "A-대형" => "월 10만 회 이상. 유입은 크지만 네이버 증권·뉴스 같은 포털 서비스가 최상단을 차지해 블로그가 올라가기 어렵습니다. 지표가 좋아 보여도 실제로는 힘든 경우가 많습니다.",
            "B-중형" => "월 1천~10만 회. 현실적으로 승산이 있는 구간입니다. 여기서 고르는 것을 권합니다.",
            "C-롱테일" => "월 100~1천 회. 상위 노출은 쉽지만 하나로는 유입이 적어 여러 개를 모아야 합니다.",
            _ => "월 100회 미만. 글을 써도 볼 사람이 거의 없습니다."
        };

        private static string BuildRow(Row r)
        {
            var cls = r.Grade switch
            {
                "노려볼만함" => "g1",
                "해볼만함" => "g2",
                "보통" => "g3",
                "수요적음" => "g5",
                _ => "g4"
            };
            var search = r.Keyword.ToLowerInvariant();
            var sb = new StringBuilder();

            sb.AppendLine($"""
                <article class="row" data-grade="{E(r.Grade)}" data-kw="{E(search)}">
                  <div class="rowhead">
                    <div class="kw">
                      <span class="badge {cls}">{E(r.Grade)}</span>
                      <h2>{E(r.Keyword)}</h2>
                      {(r.Status != "후보" ? $"""<span class="status">{E(r.Status)}</span>""" : "")}
                    </div>
                    <div class="metrics">
                      <div class="m"><b>{(r.HasVolume ? r.Volume.ToString("N0") : "-")}</b><span>월간 검색</span></div>
                      <div class="m"><b>{r.ExactTitle}/{r.Analyzed}</b><span>제목 일치</span></div>
                      <div class="m hi"><b>{(r.Score is { } s ? s.ToString("0.#") : "-")}</b><span>기회 점수</span></div>
                    </div>
                  </div>
                  <div class="detail">
                """);

            sb.AppendLine($"""
                <div class="submetrics">
                  <span>PC {r.Pc?.ToString("N0") ?? "-"}</span>
                  <span>모바일 {r.Mobile?.ToString("N0") ?? "-"}</span>
                  <span>광고 경쟁 {E(r.AdCompetition ?? "-")}</span>
                  <span>최근 글 {r.BlogTotal:N0}</span>
                  <span>최신 글 {E(r.LatestPost ?? "-")}</span>
                </div>
                """);

            if (r.TopPosts.Count > 0)
            {
                sb.AppendLine("""<h3>지금 이 키워드로 올라온 글</h3><ol class="posts">""");
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
              function visiblePanel(){ return document.querySelector('.tabpanel:not([hidden])'); }
              function apply(){
                var panel=visiblePanel(), n=0;
                rows.forEach(function(el){
                  var inPanel = panel && panel.contains(el);
                  var ok = inPanel
                        && (!grade || el.dataset.grade===grade)
                        && (!q || el.dataset.kw.indexOf(q)>-1);
                  el.hidden=!ok; if(ok)n++;
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
              <p>월간 검색수는 네이버 검색광고 키워드도구, 경쟁·상위 글은 네이버 블로그 검색 API 기준입니다.</p>
              <p>제목 일치 = 최근 글 10개 중 제목에 그 키워드를 그대로 쓴 글 수. 네이버가 보고하는 전체 건수는
                 검색어를 쪼개 느슨하게 세므로 경쟁 지표로 쓰지 않습니다.</p>
              <p>기회 점수 = 월간 검색수 ÷ 제목 점유 비율. 클수록 수요 대비 정면으로 다룬 글이 적다는 뜻입니다.</p>
            </footer>
            </body>
            </html>
            """;

        private static string E(string s) => WebUtility.HtmlEncode(s ?? string.Empty);
    }
}
