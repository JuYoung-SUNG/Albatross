using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Albatross.Collector
{
    /// <summary>
    /// 헬스 콘텐츠(부위별 종목 도감 + 초보자 루틴)를 SQLite에 보관하고, 웹이 읽을 JSON으로 내보낸다.
    /// 웹은 정적 호스팅(Cloudflare Pages)이라 SQLite를 직접 못 읽으므로 KBO와 동일하게 JSON export 방식을 쓴다.
    /// 시드는 Slug UNIQUE + INSERT OR IGNORE라서 여러 번 실행해도 중복되지 않는다(기존 내용 보존).
    /// </summary>
    internal static class GymContentSeeder
    {
        public static async Task EnsureTablesAsync(SqliteConnection connection, CancellationToken ct)
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                -- 헬스 운동 종목 (부위별 도감)
                CREATE TABLE IF NOT EXISTS GymExercises (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Slug TEXT NOT NULL UNIQUE,
                    Name TEXT NOT NULL,
                    BodyPart TEXT NOT NULL,
                    Level TEXT,
                    Equipment TEXT,
                    TargetMuscles TEXT,
                    SetsReps TEXT,
                    StepsJson TEXT NOT NULL DEFAULT '[]',
                    TipsJson TEXT NOT NULL DEFAULT '[]',
                    MistakesJson TEXT NOT NULL DEFAULT '[]',
                    SortOrder INTEGER NOT NULL DEFAULT 0,
                    CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UpdatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                -- 루틴 (초보자 주3회 분할 등)
                CREATE TABLE IF NOT EXISTS GymRoutines (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Slug TEXT NOT NULL UNIQUE,
                    Name TEXT NOT NULL,
                    Level TEXT,
                    Goal TEXT,
                    Description TEXT,
                    SortOrder INTEGER NOT NULL DEFAULT 0,
                    CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                -- 루틴 구성 (몇 일차에 어떤 종목을 몇 세트)
                CREATE TABLE IF NOT EXISTS GymRoutineItems (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    RoutineSlug TEXT NOT NULL,
                    DayNo INTEGER NOT NULL,
                    DayName TEXT NOT NULL,
                    ExerciseSlug TEXT NOT NULL,
                    SetsReps TEXT,
                    SortOrder INTEGER NOT NULL DEFAULT 0,
                    UNIQUE(RoutineSlug, DayNo, ExerciseSlug)
                );

                CREATE INDEX IF NOT EXISTS IX_GymExercises_Part ON GymExercises(BodyPart, SortOrder);
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public static async Task<int> SeedAsync(string databasePath, CancellationToken ct)
        {
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(ct);
            await EnsureTablesAsync(connection, ct);

            await using var tx = await connection.BeginTransactionAsync(ct);
            var inserted = 0;

            var order = 0;
            foreach (var e in SampleExercises)
            {
                var cmd = connection.CreateCommand();
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText =
                    """
                    INSERT OR IGNORE INTO GymExercises
                        (Slug, Name, BodyPart, Level, Equipment, TargetMuscles, SetsReps, StepsJson, TipsJson, MistakesJson, SortOrder)
                    VALUES ($slug, $name, $part, $level, $equip, $target, $sets, $steps, $tips, $mistakes, $order);
                    """;
                cmd.Parameters.AddWithValue("$slug", e.Slug);
                cmd.Parameters.AddWithValue("$name", e.Name);
                cmd.Parameters.AddWithValue("$part", e.BodyPart);
                cmd.Parameters.AddWithValue("$level", e.Level);
                cmd.Parameters.AddWithValue("$equip", e.Equipment);
                cmd.Parameters.AddWithValue("$target", e.Target);
                cmd.Parameters.AddWithValue("$sets", e.SetsReps);
                cmd.Parameters.AddWithValue("$steps", JsonSerializer.Serialize(e.Steps));
                cmd.Parameters.AddWithValue("$tips", JsonSerializer.Serialize(e.Tips));
                cmd.Parameters.AddWithValue("$mistakes", JsonSerializer.Serialize(e.Mistakes));
                cmd.Parameters.AddWithValue("$order", order++);
                inserted += await cmd.ExecuteNonQueryAsync(ct);
            }

            order = 0;
            foreach (var r in SampleRoutines)
            {
                var cmd = connection.CreateCommand();
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText =
                    """
                    INSERT OR IGNORE INTO GymRoutines (Slug, Name, Level, Goal, Description, SortOrder)
                    VALUES ($slug, $name, $level, $goal, $desc, $order);
                    """;
                cmd.Parameters.AddWithValue("$slug", r.Slug);
                cmd.Parameters.AddWithValue("$name", r.Name);
                cmd.Parameters.AddWithValue("$level", r.Level);
                cmd.Parameters.AddWithValue("$goal", r.Goal);
                cmd.Parameters.AddWithValue("$desc", r.Description);
                cmd.Parameters.AddWithValue("$order", order++);
                inserted += await cmd.ExecuteNonQueryAsync(ct);

                var itemOrder = 0;
                foreach (var (dayNo, dayName, exerciseSlug, setsReps) in r.Items)
                {
                    var itemCmd = connection.CreateCommand();
                    itemCmd.Transaction = (SqliteTransaction)tx;
                    itemCmd.CommandText =
                        """
                        INSERT OR IGNORE INTO GymRoutineItems (RoutineSlug, DayNo, DayName, ExerciseSlug, SetsReps, SortOrder)
                        VALUES ($routine, $dayNo, $dayName, $ex, $sets, $order);
                        """;
                    itemCmd.Parameters.AddWithValue("$routine", r.Slug);
                    itemCmd.Parameters.AddWithValue("$dayNo", dayNo);
                    itemCmd.Parameters.AddWithValue("$dayName", dayName);
                    itemCmd.Parameters.AddWithValue("$ex", exerciseSlug);
                    itemCmd.Parameters.AddWithValue("$sets", setsReps);
                    itemCmd.Parameters.AddWithValue("$order", itemOrder++);
                    inserted += await itemCmd.ExecuteNonQueryAsync(ct);
                }
            }

            await tx.CommitAsync(ct);
            return inserted;
        }

        /// <summary>SQLite의 헬스 콘텐츠를 웹이 읽을 health-gym.json으로 내보낸다.</summary>
        public static async Task ExportAsync(string databasePath, string dataDir, CancellationToken ct)
        {
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(ct);
            await EnsureTablesAsync(connection, ct);

            var result = new Albatross.Shared.Models.GymContentDto();

            // 종목 도감
            var exNames = new Dictionary<string, string>(StringComparer.Ordinal);
            var exCmd = connection.CreateCommand();
            exCmd.CommandText =
                """
                SELECT Slug, Name, BodyPart, Level, Equipment, TargetMuscles, SetsReps, StepsJson, TipsJson, MistakesJson
                FROM GymExercises ORDER BY SortOrder, Id;
                """;
            await using (var reader = await exCmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    var slug = reader.GetString(0);
                    var name = reader.GetString(1);
                    exNames[slug] = name;

                    result.Exercises.Add(new Albatross.Shared.Models.GymExerciseDto
                    {
                        Slug = slug,
                        Name = name,
                        BodyPart = reader.GetString(2),
                        Level = reader.IsDBNull(3) ? null : reader.GetString(3),
                        Equipment = reader.IsDBNull(4) ? null : reader.GetString(4),
                        TargetMuscles = reader.IsDBNull(5) ? null : reader.GetString(5),
                        SetsReps = reader.IsDBNull(6) ? null : reader.GetString(6),
                        Steps = DeserializeList(reader.GetString(7)),
                        Tips = DeserializeList(reader.GetString(8)),
                        Mistakes = DeserializeList(reader.GetString(9))
                    });
                }
            }

            // 루틴 + 하루치 구성
            var routines = new List<Albatross.Shared.Models.GymRoutineDto>();
            var rCmd = connection.CreateCommand();
            rCmd.CommandText = "SELECT Slug, Name, Level, Goal, Description FROM GymRoutines ORDER BY SortOrder, Id;";
            await using (var reader = await rCmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    routines.Add(new Albatross.Shared.Models.GymRoutineDto
                    {
                        Slug = reader.GetString(0),
                        Name = reader.GetString(1),
                        Level = reader.IsDBNull(2) ? null : reader.GetString(2),
                        Goal = reader.IsDBNull(3) ? null : reader.GetString(3),
                        Description = reader.IsDBNull(4) ? null : reader.GetString(4)
                    });
                }
            }

            foreach (var routine in routines)
            {
                var itemCmd = connection.CreateCommand();
                itemCmd.CommandText =
                    """
                    SELECT DayNo, DayName, ExerciseSlug, SetsReps
                    FROM GymRoutineItems WHERE RoutineSlug = $slug
                    ORDER BY DayNo, SortOrder, Id;
                    """;
                itemCmd.Parameters.AddWithValue("$slug", routine.Slug);

                await using var reader = await itemCmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var dayNo = reader.GetInt32(0);
                    var dayName = reader.GetString(1);
                    var exSlug = reader.GetString(2);

                    var day = routine.Days.FirstOrDefault(d => d.DayNo == dayNo);
                    if (day is null)
                    {
                        day = new Albatross.Shared.Models.GymRoutineDayDto { DayNo = dayNo, DayName = dayName };
                        routine.Days.Add(day);
                    }

                    day.Items.Add(new Albatross.Shared.Models.GymRoutineItemDto
                    {
                        ExerciseSlug = exSlug,
                        ExerciseName = exNames.GetValueOrDefault(exSlug, exSlug),
                        SetsReps = reader.IsDBNull(3) ? null : reader.GetString(3)
                    });
                }
            }

            result.Routines = routines;

            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            var path = Path.Combine(dataDir, "health-gym.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result, options), ct);
        }

        private static List<string> DeserializeList(string json)
        {
            try { return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>(); }
            catch (JsonException) { return new List<string>(); }
        }

        private sealed record SeedExercise(
            string Slug, string Name, string BodyPart, string Level, string Equipment, string Target, string SetsReps,
            string[] Steps, string[] Tips, string[] Mistakes);

        private sealed record SeedRoutine(
            string Slug, string Name, string Level, string Goal, string Description,
            (int DayNo, string DayName, string ExerciseSlug, string SetsReps)[] Items);

        private static readonly SeedExercise[] SampleExercises =
        [
            new("bench-press", "벤치프레스", "가슴", "초급", "바벨", "대흉근, 삼두근, 전면 삼각근", "3~4세트 x 8~12회",
                ["벤치에 누워 눈이 바 바로 아래 오도록 위치를 잡는다.",
                 "어깨를 뒤로 모아 견갑골을 벤치에 고정하고 가슴을 편다.",
                 "어깨너비보다 약간 넓게 바를 잡고 랙에서 들어 올린다.",
                 "숨을 마시며 바를 가슴 중앙까지 천천히 내린다.",
                 "발로 바닥을 밀며 숨을 내쉬고 바를 밀어 올린다."],
                ["손목은 접히지 않게 곧게 세운다.", "팔꿈치는 몸통과 45~75도를 유지한다.", "바는 가슴에 살짝 닿을 정도까지만 내린다."],
                ["허리를 과도하게 띄우기", "바를 가슴에 튕기기", "팔꿈치를 90도로 완전히 벌려 어깨에 부담 주기"]),

            new("dumbbell-fly", "덤벨 플라이", "가슴", "초급", "덤벨", "대흉근", "3세트 x 12~15회",
                ["벤치에 누워 덤벨을 가슴 위로 들어 올린다.",
                 "팔꿈치를 살짝 굽힌 각도를 끝까지 유지한다.",
                 "원을 그리듯 양옆으로 천천히 벌린다.",
                 "가슴이 늘어나는 느낌에서 다시 모아 올린다."],
                ["무게보다 가동 범위에 집중한다.", "팔꿈치 각도를 고정하면 어깨 부담이 줄어든다."],
                ["너무 깊게 내려 어깨에 무리 주기", "팔을 완전히 펴서 관절로 버티기"]),

            new("deadlift", "데드리프트", "등", "중급", "바벨", "척추기립근, 둔근, 햄스트링, 광배근", "3~5세트 x 5~8회",
                ["발은 골반너비, 바가 발등 중앙 위에 오게 선다.",
                 "엉덩이를 뒤로 빼며 어깨너비로 바를 잡는다.",
                 "가슴을 펴고 허리를 중립으로 만든다.",
                 "바를 정강이에 붙인 채 다리로 바닥을 밀며 일어선다.",
                 "선 자세에서 엉덩이를 조이고, 같은 궤적으로 내린다."],
                ["바는 항상 몸에 가깝게 붙인다.", "허리가 아니라 다리와 엉덩이 힘으로 든다.", "목은 척추의 연장선을 유지한다."],
                ["허리를 굽힌 채로 들어 올리기", "바가 몸에서 멀어지기", "상단에서 상체를 과도하게 젖히기"]),

            new("lat-pulldown", "랫풀다운", "등", "초급", "머신", "광배근, 이두근", "3~4세트 x 10~15회",
                ["무릎 패드를 허벅지에 맞게 조절해 몸을 고정한다.",
                 "어깨너비보다 넓게 바를 잡는다.",
                 "가슴을 펴고 상체를 살짝 뒤로 기댄다.",
                 "견갑골을 아래로 내리며 바를 쇄골 쪽으로 당긴다.",
                 "천천히 원위치로 돌아간다."],
                ["팔이 아니라 등으로 당긴다는 느낌으로 한다.", "당기는 마지막에 견갑골을 모아준다."],
                ["반동으로 당기기", "목 뒤로 당기기(어깨 부상 위험)", "상체를 과도하게 눕히기"]),

            new("seated-row", "시티드 로우", "등", "초급", "케이블", "광배근, 승모근 중부, 이두근", "3~4세트 x 10~12회",
                ["발을 발판에 고정하고 무릎을 살짝 굽힌다.",
                 "가슴을 펴고 허리를 중립으로 세운다.",
                 "손잡이를 배꼽 쪽으로 당긴다.",
                 "견갑골을 모았다가 천천히 팔을 편다."],
                ["상체는 고정하고 등 근육으로만 당긴다.", "당길 때 어깨가 으쓱 올라가지 않게 한다."],
                ["상체를 앞뒤로 크게 흔들기", "팔 힘으로만 당기기"]),

            new("shoulder-press", "숄더프레스", "어깨", "초급", "덤벨", "삼각근(전/측면), 삼두근", "3~4세트 x 8~12회",
                ["등받이에 등을 붙이고 앉는다.",
                 "덤벨을 귀 높이까지 들어 올린다.",
                 "팔꿈치는 몸보다 약간 앞쪽에 둔다.",
                 "숨을 내쉬며 머리 위로 밀어 올린다.",
                 "천천히 귀 높이까지 내린다."],
                ["허리를 중립으로 유지한다.", "팔꿈치를 완전히 잠그지 않는다."],
                ["허리를 젖히며 밀어 올리기", "무거운 중량으로 반동 쓰기"]),

            new("lateral-raise", "사이드 레터럴 레이즈", "어깨", "초급", "덤벨", "측면 삼각근", "3세트 x 12~15회",
                ["덤벨을 양옆에 들고 바르게 선다.",
                 "팔꿈치를 살짝 굽힌 상태를 유지한다.",
                 "어깨 높이까지 옆으로 들어 올린다.",
                 "천천히 내리며 힘을 유지한다."],
                ["가벼운 무게로 반동 없이 한다.", "손등이 위를 향하게 들어 올린다."],
                ["무거워서 승모근으로 들기", "어깨보다 높이 올리기"]),

            new("squat", "스쿼트", "하체", "초급", "바벨", "대퇴사두근, 둔근, 햄스트링", "3~5세트 x 5~10회",
                ["바를 승모근 위에 올리고 랙에서 빼낸다.",
                 "발은 어깨너비, 발끝은 15~30도 바깥으로 둔다.",
                 "숨을 마시고 배에 힘을 준 뒤 엉덩이를 뒤로 빼며 앉는다.",
                 "허벅지가 바닥과 평행해질 때까지 내려간다.",
                 "발 전체로 바닥을 밀며 일어선다."],
                ["무릎은 항상 발끝과 같은 방향으로 향한다.", "시선은 정면, 허리는 중립을 유지한다."],
                ["무릎이 안쪽으로 모이기", "뒤꿈치가 들리기", "맨 아래에서 허리가 말리기"]),

            new("leg-press", "레그프레스", "하체", "초급", "머신", "대퇴사두근, 둔근", "3~4세트 x 10~15회",
                ["등과 엉덩이를 시트에 밀착시킨다.",
                 "발은 어깨너비로 발판 중앙에 놓는다.",
                 "안전장치를 풀고 무릎을 90도까지 굽힌다.",
                 "발뒤꿈치로 발판을 밀어낸다."],
                ["무릎을 완전히 잠그지 않는다.", "허리가 시트에서 뜨지 않는 범위까지만 내린다."],
                ["무릎을 가슴까지 내려 허리가 말리기", "발끝으로만 밀기"]),

            new("barbell-curl", "바벨컬", "팔", "초급", "바벨", "이두근", "3세트 x 10~12회",
                ["어깨너비로 바를 언더그립으로 잡는다.",
                 "팔꿈치를 옆구리에 붙여 고정한다.",
                 "팔꿈치만 접어 바를 들어 올린다.",
                 "천천히 원위치로 내린다."],
                ["내릴 때 더 천천히 통제한다.", "손목은 꺾지 않고 곧게 둔다."],
                ["허리 반동으로 들어 올리기", "팔꿈치가 앞뒤로 움직이기"]),

            new("triceps-pushdown", "트라이셉스 푸시다운", "팔", "초급", "케이블", "삼두근", "3세트 x 12~15회",
                ["케이블 바를 어깨너비로 잡는다.",
                 "팔꿈치를 옆구리에 붙여 고정한다.",
                 "팔을 아래로 완전히 편다.",
                 "천천히 시작 자세로 되돌린다."],
                ["팔꿈치 고정이 핵심이다.", "상체를 약간만 앞으로 기울인다."],
                ["어깨로 눌러 내리기", "팔꿈치가 옆으로 벌어지기"]),

            new("plank", "플랭크", "코어", "초급", "맨몸", "복직근, 복횡근, 척추기립근", "30~60초 x 3세트",
                ["팔꿈치를 어깨 바로 아래에 두고 엎드린다.",
                 "발끝으로 지지하며 몸을 들어 올린다.",
                 "머리-엉덩이-발뒤꿈치가 일직선이 되게 한다.",
                 "자세를 유지하며 편안히 호흡한다."],
                ["배꼽을 등쪽으로 당기는 느낌으로 복부에 힘을 준다.", "엉덩이 높이를 수시로 확인한다."],
                ["엉덩이가 너무 높거나 아래로 처지기", "호흡을 참기"]),
        ];

        private static readonly SeedRoutine[] SampleRoutines =
        [
            new("beginner-3day", "초보자 주 3회 분할", "초급", "근력 · 근비대",
                "헬스를 처음 시작할 때 무난한 3분할입니다. 하루 걸러 하루씩(예: 월/수/금) 진행하고, 각 운동은 정확한 자세가 될 때까지 가벼운 무게로 연습하세요.",
                [
                    (1, "1일차 · 가슴 + 삼두", "bench-press", "4세트 x 8~12회"),
                    (1, "1일차 · 가슴 + 삼두", "dumbbell-fly", "3세트 x 12~15회"),
                    (1, "1일차 · 가슴 + 삼두", "triceps-pushdown", "3세트 x 12~15회"),
                    (2, "2일차 · 등 + 이두", "lat-pulldown", "4세트 x 10~15회"),
                    (2, "2일차 · 등 + 이두", "seated-row", "3세트 x 10~12회"),
                    (2, "2일차 · 등 + 이두", "barbell-curl", "3세트 x 10~12회"),
                    (3, "3일차 · 하체 + 어깨", "squat", "4세트 x 8~10회"),
                    (3, "3일차 · 하체 + 어깨", "leg-press", "3세트 x 10~15회"),
                    (3, "3일차 · 하체 + 어깨", "shoulder-press", "3세트 x 8~12회"),
                    (3, "3일차 · 하체 + 어깨", "lateral-raise", "3세트 x 12~15회"),
                    (3, "3일차 · 하체 + 어깨", "plank", "3세트 x 30~60초"),
                ]),

            new("fullbody-2day", "시간 없을 때 주 2회 전신", "초급", "체력 유지",
                "일주일에 두 번밖에 못 갈 때 쓰는 전신 루틴입니다. 큰 근육 위주로 짧고 굵게 끝냅니다.",
                [
                    (1, "A일 · 전신", "squat", "4세트 x 8회"),
                    (1, "A일 · 전신", "bench-press", "4세트 x 8회"),
                    (1, "A일 · 전신", "seated-row", "3세트 x 10회"),
                    (1, "A일 · 전신", "plank", "3세트 x 30~60초"),
                    (2, "B일 · 전신", "deadlift", "4세트 x 5~8회"),
                    (2, "B일 · 전신", "shoulder-press", "3세트 x 10회"),
                    (2, "B일 · 전신", "lat-pulldown", "3세트 x 10~12회"),
                    (2, "B일 · 전신", "plank", "3세트 x 30~60초"),
                ]),
        ];
    }
}
