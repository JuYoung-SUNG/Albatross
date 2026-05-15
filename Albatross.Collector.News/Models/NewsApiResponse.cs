namespace Albatross.Collector.News.Models;

public record NewsApiResponse(string Status, int TotalResults, IEnumerable<Article> Articles);

public record Article(string? Author, string? Title, string? Description, string? Url, string? UrlToImage, DateTimeOffset? PublishedAt, string? Content);
