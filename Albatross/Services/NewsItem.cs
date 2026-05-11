using System;

namespace Albatross.Services
{
    public class NewsItem
    {
        public string Title { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public DateTime PublishedAt { get; set; }
    }
}
