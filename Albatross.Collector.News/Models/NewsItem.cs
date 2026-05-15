namespace Albatross.Collector.News.Models;

public class NewsItem
{
    public NewsItem(string id, string title, string summary, string url, DateTimeOffset publishedAt)
    {
        Id = id;
        Title = title;
        Summary = summary;
        Url = url;
        PublishedAt = publishedAt;
    }

    public string Id { get; init; }
    public string Title { get; init; }
    public string Summary { get; init; }
    public string Url { get; init; }
    public DateTimeOffset PublishedAt { get; init; }
    public string Source { get; set; } = string.Empty;
    public string Category { get; set; } = "기타";
    public string Country { get; set; } = "한국";
    public string? ImageUrl { get; set; }
}
