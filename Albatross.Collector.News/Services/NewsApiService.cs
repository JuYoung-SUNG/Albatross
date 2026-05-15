using System;
using System.Linq;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Albatross.Collector.News.Models;
using Microsoft.Extensions.Configuration;

namespace Albatross.Collector.News.Services;

public class NewsApiService : INewsService
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _endpoint;

    public NewsApiService(HttpClient http, IConfiguration config)
    {
        _http = http;
        _apiKey = config["NewsApi:ApiKey"] ?? string.Empty;
        _endpoint = config["NewsApi:Endpoint"] ?? "https://newsapi.org/v2/top-headlines";
    }

    public async Task<IEnumerable<NewsItem>> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            // No API key configured — return sample data so frontend can display during local development
            return new[] {
                new NewsItem("1", "Sample News 1", "Summary of sample news 1", "https://example.com/news1", DateTimeOffset.UtcNow),
                new NewsItem("2", "Sample News 2", "Summary of sample news 2", "https://example.com/news2", DateTimeOffset.UtcNow)
            };
        }

        var query = $"?country=us&apiKey={Uri.EscapeDataString(_apiKey)}";
        var url = _endpoint + query;

        var response = await _http.GetFromJsonAsync<NewsApiResponse>(url, cancellationToken);
        if (response == null || response.Articles == null)
            return Enumerable.Empty<NewsItem>();

        return response.Articles.Select(a => new NewsItem(a.Url ?? Guid.NewGuid().ToString(), a.Title ?? string.Empty, a.Description ?? string.Empty, a.Url ?? string.Empty, a.PublishedAt ?? DateTimeOffset.UtcNow));
    }
}
