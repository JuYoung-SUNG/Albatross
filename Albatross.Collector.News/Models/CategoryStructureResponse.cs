using System;
using System.Collections.Generic;

namespace Albatross.Collector.News.Models;

public record CategoryAnalysisResult(
    string ReqPrompt,
    List<HierarchicalCategory> Categories
);

public record CategoryStructureResponse(
    List<HierarchicalCategory> Categories);

public record HierarchicalCategory(
    string Level1, // 대분류
    string Level2, // 중분류
    string Level3, // 소분류
    string Summary // 해당 카테고리 요약
);
