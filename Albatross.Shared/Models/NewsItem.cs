using System;

namespace Albatross.Shared.Models
{
    public class RelatedArticle
    {
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
    }

    public class NewsItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string? ImageUrl { get; set; }
        public string Category { get; set; } = "일반";
        public string PublishedAt { get; set; } = string.Empty;
        public List<RelatedArticle> RelatedArticles { get; set; } = new();
        public List<string> RelatedUrls { get; set; } = new();
    }
}
