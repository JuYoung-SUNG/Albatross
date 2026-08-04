using System;
using System.Collections.Generic;

namespace Albatross.Collector.News.Models;

public record NaverNewsResponse(
    string LastBuildDate,
    int Total,
    int Start,
    int Display,
    IEnumerable<NaverNewsItem> Items);

public record NaverNewsItem(
    string Title,
    string Originallink,
    string Link,
    string Description,
    string PubDate);
