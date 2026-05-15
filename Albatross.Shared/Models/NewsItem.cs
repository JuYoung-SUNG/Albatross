using System;

namespace Albatross.Shared.Models
{
    public class NewsItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Title { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string? Url { get; set; }
        public string? ImageUrl { get; set; }
        public string Source { get; set; } = string.Empty;
        public DateTime PublishedAt { get; set; }
    }
}
