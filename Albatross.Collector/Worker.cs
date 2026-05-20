using System;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Albatross.Collector.News.Services;

namespace Albatross.Collector
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly INewsService _news;
        private readonly IConfiguration _config;

        public Worker(ILogger<Worker> logger, INewsService news, IConfiguration config)
        {
            _logger = logger;
            _news = news;
            _config = config;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Collector worker starting");

            var args = Environment.GetCommandLineArgs();
            var singleRun = args.Contains("--once");

            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation(" =================== START ===============================");
                try
                {
                    _logger.LogInformation(" ------------ Chatper 1 -----------------------");
                    _logger.LogInformation("Collector tick at: {time}", DateTimeOffset.Now);

                    var items = await _news.GetLatestAsync(stoppingToken);
                    var count = items?.Count() ?? 0;
                    _logger.LogInformation(" ------------ Chatper 2 -----------------------");
                    _logger.LogInformation("Fetched {count} news items", count);

                    _logger.LogInformation(" ------------ Chatper 3 -----------------------");
                    if (count > 0)
                    {
                        try
                        {
                            _logger.LogInformation(" ------------ Chatper 4 -----------------------");
                            // [수정] 실행 환경에 상관없이 안전하게 경로를 계산하는 로직
                            string baseDir = AppContext.BaseDirectory;
                            string dataDir;

                            // 로컬 개발 환경(bin/Debug...)인 경우 상위 솔루션 경로 계산
                            if (baseDir.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                            {
                                var solutionRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
                                dataDir = Path.Combine(solutionRoot, "Albatross.Web", "wwwroot", "data");
                            }
                            else
                            {
                                dataDir = Path.Combine(baseDir, "data");
                            }
                            _logger.LogInformation(" ------------ Chatper 5 -----------------------");
                            Directory.CreateDirectory(dataDir);
                            var outPath = Path.Combine(dataDir, "news.json");

                            var opts = new System.Text.Json.JsonSerializerOptions
                            {
                                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                                WriteIndented = true
                            };
                            _logger.LogInformation(" ------------ Chatper 6 -----------------------");
                            var dto = items.Select(i => new { id = i.Id, title = i.Title, summary = i.Summary, url = i.Url, publishedAt = i.PublishedAt, source = i.Source, category = i.Category, country = i.Country, imageUrl = i.ImageUrl });
                            string jsonString = System.Text.Json.JsonSerializer.Serialize(dto, opts);
                            _logger.LogInformation("💾 [검증 1] 직렬화된 JSON 크기: {length} 글자", jsonString.Length);

                            // 2. 물리 파일 쓰기 실행
                            await System.IO.File.WriteAllTextAsync(outPath, jsonString, stoppingToken);
                            // 3. [핵심] 윈도우 시스템에 파일이 실제로 생성되었고 용량이 얼마인지 직접 재확인
                            
                            FileInfo fileInfo = new FileInfo(outPath);
                            if (fileInfo.Exists)
                            {
                                _logger.LogInformation("✅ [검증 2] 파일 쓰기 최종 성공! 실제 파일 크기: {size} Bytes, 경로: {path}", fileInfo.Length, outPath);
                            }
                            else
                            {
                                _logger.LogError("❌ [검증 2] OS가 성공했다고 했으나, 실제 경로에 파일이 존재하지 않습니다!");
                            }
                            _logger.LogInformation("Successfully wrote news file to: {path}", outPath);
                            _logger.LogInformation(" ------------ Chatper 7 -----------------------");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "❌ [검증 2] 파일 쓰기 작업 중 윈도우 OS 에러 발생");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during collection");
                }

                if (singleRun)
                {
                    _logger.LogInformation("Single-run mode, exiting");
                    break;
                }

                // [수정] 디버그 모드일 때는 빠른 테스트를 위해 10초 주기로 변경, 운영 환경은 설정값(기본 10분) 추종
                int intervalMinutes = _config.GetValue<int>("Collector:IntervalMinutes", 10);
                TimeSpan delayTime = TimeSpan.FromMinutes(intervalMinutes);

#if DEBUG
                delayTime = TimeSpan.FromSeconds(10);
                _logger.LogInformation("Debug mode active: Next tick will run in 10 seconds.");
#endif

                await Task.Delay(delayTime, stoppingToken);
                _logger.LogInformation(" =================== END ===============================");
            }

            _logger.LogInformation("Collector worker stopping");
        }
    }
}