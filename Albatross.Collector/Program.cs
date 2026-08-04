using Albatross.Collector;
using Albatross.Collector.News.Services;
using Serilog;
using Serilog.Events;

System.Console.OutputEncoding = System.Text.Encoding.UTF8;

// 1. 수집기 전용 로그 경로 세팅 (실행 경로 내부 logs 폴더)
string baseDir = AppDomain.CurrentDomain.BaseDirectory;
string logFilePath = Path.Combine(baseDir, "logs", "collector_log-.txt");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    // 🔥 [이 줄을 추가!] 마이크로소프트 닷넷 호스팅 시스템 로그는 '경고(Warning)' 이상만 보겠다!
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .WriteTo.Debug()
    .WriteTo.File(
        path: logFilePath,
        rollingInterval: RollingInterval.Day,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
        encoding: System.Text.Encoding.UTF8
    )
    .CreateLogger();

try
{
    Log.Information("==================================================");
    Log.Information("Albatross Collector Service Starting...");
    Log.Information($"Collector Log Path: {logFilePath}");
    Log.Information("==================================================");

    IHost host = Host.CreateDefaultBuilder(args)
        .UseWindowsService(options =>
        {
            options.ServiceName = "AlbatrossCollector";
        })
        .ConfigureServices((context, services) =>
        {
            // 2. 표준 로깅을 Serilog로 대체
            services.AddLogging(loggingBuilder =>
            {
                loggingBuilder.ClearProviders();
                loggingBuilder.AddSerilog(dispose: true);
            });

            // 뉴스 소스 등록 (여러 개 등록하면 Worker가 IEnumerable<INewsService>로 모두 받아 병합 수집)
            services.AddHttpClient<INewsService, NaverNewsService>();
            services.AddHttpClient<INewsService, RssNewsScraperService>();
            // 날짜별 뉴스 소급(--backfill-news)에서 직접 호출할 수 있도록 concrete 타입으로도 별도 등록
            services.AddHttpClient<NaverNewsService>();

            // 로컬 AI 분류 서비스 등록 (CPU 추론은 GPU보다 느려 기본 100초 타임아웃을 넘길 수 있음)
            services.AddHttpClient<GemmaClassificationService>(client =>
            {
                client.Timeout = TimeSpan.FromMinutes(5);
            });

            // KBO 공식 사이트(팀 순위 / 선수 기록) 수집기
            services.AddHttpClient<KboOfficialSiteService>();

            // 네이버 DataLab 검색어 트렌드 (키워드 검색량 크로스체크용)
            services.AddHttpClient<NaverDataLabService>();

            // 뉴스 키워드 추출기 (RawNews 급상승 통계 + 로컬 Gemma 정제 + DataLab 검색량)
            services.AddSingleton<KeywordExtractionService>();

            services.AddSingleton<Worker>();
            services.AddHostedService<Worker>(sp => sp.GetRequiredService<Worker>());
        })
        .Build();

    // --reclassify 인자 처리
    if (args.Length >= 2 && args[0] == "--reclassify")
    {
        var newsId = args[1];
        Log.Information("Manual reclassification requested for newsId: {newsId}", newsId);
        
        var worker = host.Services.GetRequiredService<Worker>();
        await worker.ClassifySingleNewsAsync(newsId, CancellationToken.None);
        return;
    }

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Collector 호스트가 예기치 않게 종료되었습니다.");
    throw;
}
finally
{
    Log.CloseAndFlush();
}