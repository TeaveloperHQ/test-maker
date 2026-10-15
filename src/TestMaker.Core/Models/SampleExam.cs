namespace TestMaker.Core.Models;

/// <summary>표기법과 문항 유형을 모두 보여 주는 예시 시험. 새 시험 JSON의 출발점으로 쓴다.</summary>
public static class SampleExam
{
    public static Exam Create() => new()
    {
        Info = new ExamInfo
        {
            Title = "2026학년도 2학기 중간고사",
            Subject = "과학",
            Grade = "1학년",
            School = "OO중학교",
            Date = "2026. 10. 20.(화) 2교시",
            DurationMinutes = 45,
        },
        Questions =
        [
            new()
            {
                Stem = "다음 중 물질의 상태 변화에 대한 설명으로 **옳은** 것은?",
                Points = 4,
                Choices =
                [
                    "얼음이 녹을 때 열을 방출한다.",
                    "물이 끓을 때 분자 사이의 거리가 가까워진다.",
                    "드라이아이스가 작아지는 것은 승화 현상이다.",
                    "응결이 일어날 때 분자의 운동이 활발해진다.",
                    "상태가 변하면 물질의 성질이 바뀐다.",
                ],
                Answer = [3],
                Explanation = "드라이아이스는 고체에서 기체로 바로 변하는 승화가 일어난다.",
                Meta = new() { Unit = "Ⅳ. 물질의 상태 변화", Difficulty = "하" },
            },
            new()
            {
                Stem = "그림과 같이 질량이 $m$인 물체에 힘 $F$를 가했을 때, 가속도 $a$를 옳게 나타낸 것은?",
                Points = 4,
                Choices = ["$F over m$", "$m over F$", "$Fm$", "$F+m$", "$F-m$"],
                Answer = [1],
                Explanation = "뉴턴 운동 제2법칙 $F=ma$ 에서 $a = F over m$ 이다.",
            },
            new()
            {
                Stem = "<보기>에서 옳은 것만을 있는 대로 고른 것은?",
                Points = 5,
                Box = new BoxContent
                {
                    Lines =
                    [
                        "ㄱ. 기체의 압력은 기체 분자가 벽에 충돌하여 나타난다.",
                        "ㄴ. 온도가 일정할 때 압력을 높이면 기체의 부피는 커진다.",
                        "ㄷ. 압력이 일정할 때 온도를 높이면 기체의 부피는 커진다.",
                    ],
                },
                Choices = ["ㄱ", "ㄴ", "ㄱ, ㄷ", "ㄴ, ㄷ", "ㄱ, ㄴ, ㄷ"],
                Answer = [3],
                Explanation = "ㄴ. 보일 법칙에 따라 압력을 높이면 부피는 작아진다.",
            },
            new()
            {
                Type = QuestionType.ShortAnswer,
                Stem = "물질이 고체에서 기체로 바로 변하는 현상을 무엇이라고 하는지 쓰시오.",
                Points = 3,
                AnswerText = "승화",
            },
            new()
            {
                Type = QuestionType.Essay,
                Stem = "여름철 자전거 바퀴에 바람을 가득 넣어 두면 __㉠바퀴가 터지기도 한다.__\n밑줄 친 ㉠의 까닭을 입자 운동과 관련지어 서술하시오.",
                Points = 6,
                AnswerLines = 4,
                AnswerText = "온도가 높아지면 기체 입자의 운동이 활발해져 바퀴 안쪽 벽에 충돌하는 횟수와 세기가 커지므로 압력이 커지기 때문이다.",
                Explanation = "채점 기준: 입자 운동이 활발해짐(3점), 압력 증가(3점).",
            },
        ],
    };
}
