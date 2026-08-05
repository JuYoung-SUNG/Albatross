using System.Collections.Generic;

namespace Albatross.Shared.Models
{
    /// <summary>헬스 운동 종목 1개 (부위별 도감의 항목)</summary>
    public class GymExerciseDto
    {
        public string Slug { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string BodyPart { get; set; } = string.Empty;   // 가슴/등/어깨/하체/팔/코어
        public string? Level { get; set; }                     // 초급/중급/고급
        public string? Equipment { get; set; }                 // 바벨/덤벨/머신/케이블/맨몸
        public string? TargetMuscles { get; set; }             // 대흉근, 삼두근 ...
        public string? SetsReps { get; set; }                  // 3~4세트 x 8~12회

        public List<string> Steps { get; set; } = new();       // 자세 단계
        public List<string> Tips { get; set; } = new();        // 포인트
        public List<string> Mistakes { get; set; } = new();    // 흔한 실수
    }

    /// <summary>루틴에서 하루치에 포함된 운동 1개</summary>
    public class GymRoutineItemDto
    {
        public string ExerciseSlug { get; set; } = string.Empty;
        public string ExerciseName { get; set; } = string.Empty;
        public string? SetsReps { get; set; }
    }

    /// <summary>루틴의 하루치 (예: 1일차 가슴+삼두)</summary>
    public class GymRoutineDayDto
    {
        public int DayNo { get; set; }
        public string DayName { get; set; } = string.Empty;
        public List<GymRoutineItemDto> Items { get; set; } = new();
    }

    /// <summary>초보자 루틴 등 프로그램 1개</summary>
    public class GymRoutineDto
    {
        public string Slug { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Level { get; set; }
        public string? Goal { get; set; }
        public string? Description { get; set; }
        public List<GymRoutineDayDto> Days { get; set; } = new();
    }

    /// <summary>헬스 페이지가 읽는 전체 콘텐츠 (health-gym.json)</summary>
    public class GymContentDto
    {
        public List<GymRoutineDto> Routines { get; set; } = new();
        public List<GymExerciseDto> Exercises { get; set; } = new();
    }
}
