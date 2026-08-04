using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Albatross.Collector.News.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Albatross.Collector.News.Services;

public class GemmaClassificationService
{
    private readonly HttpClient _http;
    private readonly ILogger<GemmaClassificationService> _logger;
    private readonly IConfiguration _config;
    private readonly string _gemmaEndpoint;
    private readonly string _gemmaModel;
    private readonly int _numCtx;
    private readonly int _numPredict;
    private readonly int _numGpu;

    public GemmaClassificationService(HttpClient http, ILogger<GemmaClassificationService> logger, IConfiguration config)
    {
        _http = http;
        _logger = logger;
        _config = config;
        _gemmaEndpoint = _config["Gemma:Endpoint"] ?? "http://localhost:11434/api/generate";
        _gemmaModel = _config["Gemma:Model"] ?? "gemma4:e4b";
        _numCtx = _config.GetValue<int>("Gemma:NumCtx", 8192);
        _numPredict = _config.GetValue<int>("Gemma:NumPredict", 8192);
        _numGpu = _config.GetValue<int>("Gemma:NumGpu", 0);
    }

    private async Task<string> GetLevel1CategoriesAsync(string databasePath, CancellationToken ct)
    {
        var categories = new List<string>();

        try
        {
            if (!File.Exists(databasePath))
            {
                _logger.LogWarning("Database file not found at {path}. Returning default categories.", databasePath);
                return "정치, 경제, 사회, IT, 과학, 스포츠, 연예";
            }

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(ct);

            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='Categories';";
            var tableExists = await cmd.ExecuteScalarAsync(ct) != null;

            if (!tableExists)
            {
                _logger.LogWarning("Table 'Categories' does not exist yet.");
                return "정치, 경제, 사회, IT, 과학, 스포츠, 연예";
            }

            cmd.CommandText = "SELECT CategoryName FROM Categories WHERE Level = 1;";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                categories.Add(reader.GetString(0));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch level 1 categories from DB. Returning defaults.");
        }

        return categories.Count > 0 ? string.Join(", ", categories) : "정치, 경제, 사회, IT, 과학, 스포츠, 연예";
    }

    public async Task<string> CallGemmaJsonAsync(string prompt, CancellationToken ct)
    {
        try
        {
            var requestBody = new
            {
                model = _gemmaModel,
                prompt = prompt,
                stream = false,
                format = "json",
                options = new { num_ctx = _numCtx, num_predict = _numPredict, num_gpu = _numGpu }
            };

            var contentString = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(_gemmaEndpoint, contentString, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Gemma API 호출 실패: {Status}", response.StatusCode);
                return string.Empty;
            }

            var responseStream = await response.Content.ReadAsStreamAsync(ct);
            var jsonResponse = await JsonSerializer.DeserializeAsync<JsonElement>(responseStream, cancellationToken: ct);
            return jsonResponse.GetProperty("response").GetString()?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gemma API 호출 중 오류 발생");
            return string.Empty;
        }
    }

    /// <summary>
    /// 키워드 추출 등 짧은 프롬프트용 Gemma 호출 — num_gpu를 보내지 않아 Ollama가 GPU에 올리도록 한다.
    /// (설정 Gemma:NumGpu=0(CPU 강제)은 이 머신에서 모델 가중치 버퍼 OOM으로 실패하므로 우회.)
    /// num_ctx도 4096으로 낮춰 불필요한 메모리 예약을 줄인다. Ollama가 error 필드를 주면 빈 문자열 반환.
    /// </summary>
    public async Task<string> CallGemmaJsonGpuAsync(string prompt, CancellationToken ct)
    {
        try
        {
            var requestBody = new
            {
                model = _gemmaModel,
                prompt = prompt,
                stream = false,
                format = "json",
                options = new { num_ctx = 4096, num_predict = 2048, temperature = 0.1 }
            };

            var contentString = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(_gemmaEndpoint, contentString, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Gemma(GPU) API 호출 실패: {Status}", response.StatusCode);
                return string.Empty;
            }

            var responseStream = await response.Content.ReadAsStreamAsync(ct);
            var jsonResponse = await JsonSerializer.DeserializeAsync<JsonElement>(responseStream, cancellationToken: ct);
            if (jsonResponse.TryGetProperty("error", out var err))
            {
                _logger.LogWarning("Gemma(GPU) 응답 오류: {err}", err.GetString());
                return string.Empty;
            }
            return jsonResponse.TryGetProperty("response", out var r) ? r.GetString()?.Trim() ?? string.Empty : string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gemma(GPU) API 호출 중 오류 발생");
            return string.Empty;
        }
    }

    /// <summary>
    /// 특정 날짜의 KBO 관련 기사 원문들을 참고해, 저작권 문제가 없도록 완전히 새로 쓴 하이라이트 문장을 생성한다.
    /// </summary>
    public async Task<string> SummarizeDateHighlightAsync(string gameDate, List<(string Title, string Content)> articles, CancellationToken ct)
    {
        if (articles.Count == 0) return string.Empty;

        var articlesText = string.Join("\n\n", articles.Select((a, i) => $"[기사 {i + 1}] 제목: {a.Title}\n내용: {a.Content}"));

        var prompt = $$"""
            당신은 KBO(한국프로야구) 전문 스포츠 기자입니다. 아래는 {{gameDate}}자 KBO 관련 기사 원문들입니다.

            [매우 중요한 규칙]
            - 원문 문장을 그대로 베끼거나 단어만 살짝 바꿔 쓰지 마세요. 저작권 문제가 있으므로 반드시 완전히 새로운 문장 구조로 재작성해야 합니다.
            - 원문에 없는 사실을 지어내지 마세요. 사실관계(팀명, 선수명, 스코어, 결과)만 참고하세요.
            - 여러 기사 내용을 종합해서 그날의 KBO 주요 소식을 3~5문장으로 요약하세요.

            반드시 아래의 깨끗한 JSON 객체 형식으로만 응답해 주세요. 부연 설명이나 마크다운 백틱은 절대 포함하지 마십시오.

            형식:
            {
              "highlight": "..."
            }

            [원문 기사 목록]
            {{articlesText}}

            응답 JSON:
            """;

        var raw = await CallGemmaJsonAsync(prompt, ct);
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.TryGetProperty("highlight", out var h) ? h.GetString()?.Trim() ?? string.Empty : string.Empty;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("날짜별 하이라이트 JSON 파싱 실패: {msg}. 원본: {raw}", ex.Message, raw);
            return string.Empty;
        }
    }

    public async Task<CategoryAnalysisResult> Classify3LevelsAsync(string newsId, string title, string content, string databasePath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(content))
            return new CategoryAnalysisResult(string.Empty, new List<HierarchicalCategory>());

        var level1Categories = await GetLevel1CategoriesAsync(databasePath, ct);

        var prompt = $$"""
            당신은 뉴스 고도 정제 및 카테고리 분류 전문가입니다. 다음 뉴스 기사의 제목과 본문을 분석하여, [3단계 계층적 카테고리 체계]에 맞게 분류해 주세요.

            [1단계 카테고리 (고정)]
            {{level1Categories}}

            [3단계 정의 기준]
            - Level 1: 위 고정 카테고리 중 하나를 선택.
            - Level 2: Level 1 내 주요 분야.
            - Level 3: Level 2 내 구체적 사안.
            - Summary: 해당 카테고리 분류에 기반한 기사 요약.

            반드시 아래의 깨끗한 JSON 객체 형식으로만 응답해 주세요. 부연 설명이나 마크다운 백틱은 절대 포함하지 마십시오.

            형식:
            {
              "categories": [
                {
                  "level1": "...",
                  "level2": "...",
                  "level3": "...",
                  "summary": "..."
                }
              ]
            }

            [뉴스 제목]
            {{title}}

            [뉴스 본문]
            {{content}}

            응답 JSON:
            """;

        try
        {
            var requestBody = new
            {
                model = _gemmaModel,
                prompt = prompt,
                stream = false,
                format = "json",
                options = new { num_ctx = _numCtx, num_predict = _numPredict, num_gpu = _numGpu }
            };

            var contentString = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(_gemmaEndpoint, contentString, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Gemma API 호출 실패: {Status}", response.StatusCode);
                return new CategoryAnalysisResult(prompt, new List<HierarchicalCategory>());
            }

            var responseStream = await response.Content.ReadAsStreamAsync(ct);
            var jsonResponse = await JsonSerializer.DeserializeAsync<JsonElement>(responseStream, cancellationToken: ct);
            var resultText = jsonResponse.GetProperty("response").GetString()?.Trim() ?? string.Empty;
            
            if (resultText.StartsWith("```"))
            {
                resultText = resultText.Split("\n").Where(line => !line.Trim().StartsWith("```")).Aggregate((a, b) => a + "\n" + b).Trim();
            }

            // [보정] 유효한 JSON 구간만 추출 (가장 마지막 '}' 이후의 노이즈 제거)
            int lastBrace = resultText.LastIndexOf('}');
            if (lastBrace != -1)
            {
                resultText = resultText.Substring(0, lastBrace + 1);
            }

            try 
            {
                var root = JsonSerializer.Deserialize<CategoryStructureResponse>(resultText, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (root?.Categories != null && root.Categories.Count > 0)
                {
                    return new CategoryAnalysisResult(prompt, root.Categories);
                }
            }
            catch (JsonException)
            {
                // [복구 시도] 닫히지 않은 문자열/객체/배열 보정 후 재파싱
                string repaired = resultText;
                
                // 1. 마지막 닫히지 않은 따옴표 체크 (요약문이 잘린 경우)
                int quoteCount = repaired.Count(c => c == '\"');
                if (quoteCount % 2 != 0) repaired += "\"";

                // 2. 구조적 괄호 닫기 (열린 괄호와 닫힌 괄호 개수 차이 계산)
                int openBrace = repaired.Count(c => c == '{');
                int closeBrace = repaired.Count(c => c == '}');
                int openBracket = repaired.Count(c => c == '[');
                int closeBracket = repaired.Count(c => c == ']');
                
                for (int i = 0; i < (openBracket - closeBracket); i++) repaired += "]";
                for (int i = 0; i < (openBrace - closeBrace); i++) repaired += "}";
                
                try 
                {
                    var root = JsonSerializer.Deserialize<CategoryStructureResponse>(repaired, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (root?.Categories != null && root.Categories.Count > 0)
                    {
                        return new CategoryAnalysisResult(prompt, root.Categories);
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogError("Gemma AI 3단계 카테고리 응답 파싱 최종 실패. 보정문: {Result}. 에러: {Msg}", repaired, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gemma AI 3단계 동적 분류 중 알 수 없는 오류 발생");
        }

        return new CategoryAnalysisResult(prompt, new List<HierarchicalCategory>());
    }
}
