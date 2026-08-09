using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Albatross.Collector.News.Services;

/// <summary>
/// KLPGA(한국여자프로골프협회) 공식 사이트의 대회 일정 데이터를 가져온다.
///
/// 왜 이 경로인가 — klpga.co.kr 은 화면 자체는 자바스크립트로 그리지만,
/// 그 화면이 부르는 /ajax/tourInfo/getGameList 가 대회 정보를 통째로 JSON으로 돌려준다.
/// 총상금·개최코스·라운드 수·출전 인원·우승자·디펜딩 챔피언까지 한 번에 들어 있어
/// 화면을 긁는 것보다 훨씬 안정적이다.
///
/// KPGA(남자)는 같은 방식이 통하지 않는다. Next.js 앱이고 실제 API 호스트(erp.kpga.co.kr)가
/// 외부에서 응답하지 않아 현재는 수집 대상에서 제외한다.
/// </summary>
public class KlpgaTourService
{
    private const string Endpoint = "https://klpga.co.kr/ajax/tourInfo/getGameList";
    private const string Referer = "https://klpga.co.kr/web/schedule/schedule";

    private readonly HttpClient _http;
    private readonly ILogger<KlpgaTourService> _logger;

    public KlpgaTourService(HttpClient http, ILogger<KlpgaTourService> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>대회 하나. 협회가 주지 않는 값은 null로 둔다(임의로 채우지 않는다).</summary>
    public sealed record Tournament(
        string GameCode,
        string Title,
        string? EngTitle,
        string TourType,
        string StartDate,      // yyyy-MM-dd
        string EndDate,
        long? PrizeMoney,
        string? MoneyUnit,
        string? Course,
        int? Rounds,
        int? Par,
        int? EntryCount,
        string? WinnerName,
        string? DefendingName);

    /// <summary>
    /// 지정 연도의 대회 목록. 선발전·실기평가·이론교육은 "대회"가 아니라 제외한다.
    /// </summary>
    public async Task<List<Tournament>> GetTournamentsAsync(int year, CancellationToken ct)
    {
        var result = new List<Tournament>();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["year"] = year.ToString() })
            };
            req.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");
            req.Headers.Add("Referer", Referer);
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");

            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("[KLPGA] {year}년 일정 조회 실패 {code}", year, (int)res.StatusCode);
                return result;
            }

            var body = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("gameList", out var list)) return result;

            foreach (var g in list.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();

                var tourType = Str(g, "tourType") ?? "";
                if (!IsRealTournament(tourType)) continue;

                var start = Str(g, "startDate");
                if (start is null || !start.StartsWith(year.ToString(), StringComparison.Ordinal)) continue;

                result.Add(new Tournament(
                    Str(g, "gameCode") ?? "",
                    Str(g, "gameTitle") ?? "",
                    Str(g, "gameEngTitle"),
                    tourType,
                    FormatDate(start),
                    FormatDate(Str(g, "endDate") ?? start),
                    ParseMoney(Str(g, "prizeMoney")),
                    Str(g, "moneyUnit"),
                    Blank(Str(g, "courseText")),
                    Num(g, "totalRound"),
                    Num(g, "totalPar"),
                    Num(g, "totalEntryCnt"),
                    Blank(Str(g, "winnerName")),
                    Blank(Str(g, "defendingName"))));
            }

            _logger.LogInformation("[KLPGA] {year}년 대회 {n}개 수집", year, result.Count);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("[KLPGA] {year}년 조회 중 오류: {msg}", year, ex.Message);
        }
        return result;
    }

    /// <summary>
    /// 실제 "대회"인 유형만 통과시킨다.
    ///   RE 정규투어 / DR 드림투어(2부) / JT 점프투어(3부) / SN 챔피언스투어 / WT 해외 공동주관 / NR 특별
    /// 제외: SE 시드순위전, TE 준회원 실기평가, TS 이론교육, PO 정회원 선발전
    /// </summary>
    private static bool IsRealTournament(string tourType) =>
        tourType is "RE" or "DR" or "JT" or "SN" or "WT" or "NR";

    private static string? Str(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            _ => null
        };
    }

    private static int? Num(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n > 0 ? n : null;
        return int.TryParse(Str(el, name), out var p) && p > 0 ? p : null;
    }

    private static long? ParseMoney(string? s) =>
        long.TryParse(s?.Replace(",", ""), out var v) && v > 0 ? v : null;

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>"20260611" → "2026-06-11"</summary>
    private static string FormatDate(string yyyymmdd) =>
        yyyymmdd.Length == 8
            ? $"{yyyymmdd[..4]}-{yyyymmdd.Substring(4, 2)}-{yyyymmdd.Substring(6, 2)}"
            : yyyymmdd;
}
