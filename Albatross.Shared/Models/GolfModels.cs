using System.Collections.Generic;

namespace Albatross.Shared.Models
{
    /// <summary>
    /// 골프 연습장 1곳. LLM이 블로그 후기·홈페이지 텍스트에서 뽑아낸 "사실 정보"만 담는다.
    /// (후기 문장을 그대로 옮기지 않고 요금·좌타석·드라이버 가능 여부 같은 팩트만 정제해 저장)
    /// </summary>
    public class GolfRangeDto
    {
        public string Slug { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;

        public string? Type { get; set; }        // 파3 / 야외인도어 / 실내 / 스크린
        public string? Region { get; set; }      // 수도권 / 강원 / 충청 / 영남 / 호남 / 제주
        public string? City { get; set; }        // 시·군·구
        public string? Address { get; set; }
        public string? Phone { get; set; }
        public string? Hours { get; set; }       // 영업시간

        public string? Price { get; set; }       // 일일 타석 요금 (예: 평일 2만원 / 주말 2.5만원)
        public string? LeftHanded { get; set; }  // 좌타석 (예: 2개 보유 / 없음 / 미확인)
        public string? DriverAllowed { get; set; } // 드라이버 (예: 가능 / 불가 / 일부 타석만)
        public string? Parking { get; set; }     // 주차 (예: 2시간 무료)

        public string? Summary { get; set; }     // 한 줄 요약
        public List<string> Highlights { get; set; } = new();  // 장점·특징
        public List<string> Cautions { get; set; } = new();    // 주의할 점
        public List<string> SourceUrls { get; set; } = new();  // 출처(홈페이지·후기 글)

        public string? UpdatedAt { get; set; }   // 정보 기준 시점
    }

    /// <summary>골프 사이트가 읽는 전체 데이터 (golf-ranges.json)</summary>
    public class GolfContentDto
    {
        public List<GolfRangeDto> Ranges { get; set; } = new();
    }
}
