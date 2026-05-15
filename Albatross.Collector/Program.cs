using Albatross.Collector;
using Albatross.Collector.News.Services;

IHost host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "AlbatrossCollector";
    })
    .ConfigureServices((context, services) =>
    {
        // RSS 스크래퍼를 기본 INewsService로 등록 (API 키 불필요)
        services.AddHttpClient<INewsService, RssNewsScraperService>();

        // NewsAPI.org 를 사용하려면 아래 줄로 교체 후 ApiKey를 user-secrets 또는 환경변수로 설정
        // services.AddHttpClient<INewsService, NewsApiService>();

        services.AddHostedService<Worker>();
    })
    .Build();

await host.RunAsync();
