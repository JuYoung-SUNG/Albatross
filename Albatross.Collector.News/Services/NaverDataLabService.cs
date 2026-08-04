using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Albatross.Collector.News.Services;

/// <summary>
/// 네이버 DataLab 검색어 트렌드 API로 키워드의 최근 검색 관심도(상대 비율 0~100)를 가져온다.
/// 뉴스 검색과 같은 자격증명(NAVER_CLIENT_ID/SECRET)을 쓰지만, 네이버 개발자센터에서 해당 앱에
/// "데이터랩(검색어 트렌드)" API를 별도로 추가해야 인증이 통과된다. (권한 없으면 인증 실패 → 빈 결과)
/// </summary>
public class NaverDataLabService
{
    private readonly HttpClient _http;
    private readonly ILogger<NaverDataLabService> _logger;
    private const string Endpoint = "https://openapi.naver.com/v1/datalab/search";

    public NaverDataLabService(HttpClient http, ILogger<NaverDataLabService> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// 키워드별 최근 검색 관심도를 반환한다 (키워드 → 최근 7일 평균 비율 0~100).
    /// 검색 데이터가 없으면 0. API 인증/네트워크 실패 시 그 키워드는 딕셔너리에서 빠짐(→ 호출측에서 NULL 유지).
    /// </summary>
    public async Task<Dictionary<string, double>> GetSearchVolumesAsync(List<string> keywords, CancellationToken ct)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);

        var clientId = Environment.GetEnvironmentVariable("NAVER_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("NAVER_CLIENT_SECRET");
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            _logger.LogWarning("[DataLab] NAVER_CLIENT_ID/SECRET 미설정 — 검색량 크로스체크 생략");
            return result;
        }

        var endDate = DateTime.Now;
        var startDate = endDate.AddDays(-30);
        var recentCutoff = endDate.AddDays(-7);

        // DataLab은 요청당 키워드 그룹 최대 5개
        foreach (var batch in keywords.Chunk(5))
        {
            ct.ThrowIfCancellationRequested();

            var body = new
            {
                startDate = startDate.ToString("yyyy-MM-dd"),
                endDate = endDate.ToString("yyyy-MM-dd"),
                timeUnit = "date",
                keywordGroups = batch.Select(k => new { groupName = k, keywords = new[] { k } }).ToArray()
            };

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                req.Headers.Add("X-Naver-Client-Id", clientId);
                req.Headers.Add("X-Naver-Client-Secret", clientSecret);
                req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

                using var resp = await _http.SendAsync(req, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[DataLab] 요청 실패 {status} — {msg}", resp.StatusCode,
                        json.Length > 200 ? json[..200] : json);
                    // 인증 실패(권한 미설정) 등은 이후 배치도 동일하므로 중단
                    break;
                }

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("results", out var results)) continue;

                foreach (var g in results.EnumerateArray())
                {
                    var title = g.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(title) || !g.TryGetProperty("data", out var data)) continue;

                    var recentRatios = new List<double>();
                    foreach (var d in data.EnumerateArray())
                    {
                        if (!d.TryGetProperty("period", out var p) || !d.TryGetProperty("ratio", out var r)) continue;
                        if (DateTime.TryParse(p.GetString(), out var period) && period >= recentCutoff)
                            recentRatios.Add(r.GetDouble());
                    }

                    result[title] = recentRatios.Count > 0 ? Math.Round(recentRatios.Average(), 1) : 0.0;
                }

                await Task.Delay(120, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[DataLab] 검색량 조회 중 오류: {msg}", ex.Message);
                break;
            }
        }

        _logger.LogInformation("[DataLab] 검색량 조회 완료 — {n}개 키워드에 값 부여", result.Count);
        return result;
    }
}
