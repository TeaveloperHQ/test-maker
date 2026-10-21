using System.Text;

namespace TestMaker.Ai;

/// <summary>Claude에게 주는 지시문. 결과는 항상 작업 폴더의 파일로 받는다(앱이 읽고 검사한다).</summary>
public static class Prompts
{
    public const string FoundFile = "found.json";

    public static string FindQuestions(AuthoringJob job)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            너는 한국 중·고등학교 과학 선생님의 시험 출제를 돕는다. 지금 폴더는 작업 폴더다.
            아래 자료 파일을 모두 읽고(그림이면 그림을 보고, PDF면 모든 쪽을), 그 안에 있는 시험 문항을 빠짐없이 찾아라.

            자료 파일:
            """);
        foreach (var s in job.Sources)
            foreach (var f in s.ReadFiles) sb.AppendLine($"- {Slash(f)}   (원래 파일: {s.Name})");
        sb.AppendLine($$"""

            찾은 문항을 `{{FoundFile}}`에 UTF-8 JSON으로 저장하라. 형식:
            {"questions": [
              {"source": "원래 파일 이름", "number": "07", "summary": "온도가 다른 두 물체를 접촉할 때 열의 이동(보기 고르기)",
               "type": "MultipleChoice", "hasFigure": true}
            ]}
            - 자료에 나온 순서대로. number는 자료에 적힌 번호 그대로(없으면 순서대로 "1", "2" …).
            - type: 선택형 MultipleChoice, 단답형 ShortAnswer, 서술형·논술형 Essay.
            - summary: 선생님이 어떤 문항인지 알아볼 수 있게 한 줄(30자 안팎). 잘려서 일부만 보이면 끝에 "(일부 잘림)".
            - 문항이 아닌 것(표지, 유의사항, 정답표, 해설, 단원 정리)은 넣지 마라. 묶음 문항([3~4] 공통 지문)은 문항마다 따로.
            - 문항을 하나도 못 찾으면 {"questions": []}.
            파일 저장 말고는 아무것도 바꾸지 마라. 끝나면 찾은 문항 수만 한 줄로 답하라.
            """);
        return sb.ToString();
    }

    public static string MakeVariants(AuthoringJob job, FoundQuestion q)
    {
        var source = job.Sources.FirstOrDefault(s => s.Name == q.Source);
        var sb = new StringBuilder();
        sb.AppendLine($"""
            너는 한국 중·고등학교 과학 선생님의 시험 출제를 돕는다. 지금 폴더는 작업 폴더다.

            ## 할 일
            자료에서 {q.Number}번 문항("{q.Summary}")을 찾아, 그 문항을 바탕으로 시험 문항을 만들어라.
            """);
        if (source is not null)
        {
            sb.AppendLine("자료 파일:");
            foreach (var f in source.ReadFiles) sb.AppendLine($"- {Slash(f)}");
            if (source.Figures.Count > 0)
            {
                sb.AppendLine("이 자료 안에 들어 있던 그림 파일(원본 그림을 그대로 쓰거나 따라 그릴 때 이것을 보라):");
                foreach (var f in source.Figures) sb.AppendLine($"- {Slash(f)}");
            }
        }
        sb.AppendLine();
        if (q.KeepOriginal) sb.AppendLine("- 첫 문항: 원래 문항을 그대로 옮긴 것(글, 그림, 선택지, 정답 모두 원래대로).");
        if (q.Variants > 0)
        {
            sb.AppendLine($"- 변형 문항 {q.Variants}개. 변형 방식: {job.Style}{StyleHint(job.Style)}");
            sb.AppendLine("  변형끼리도 서로 달라야 한다(숫자·상황·정답 번호가 겹치지 않게).");
        }
        if (!string.IsNullOrWhiteSpace(job.Request)) sb.AppendLine($"- 선생님 요청: {job.Request.Trim()}");

        sb.AppendLine($$"""

            ## 변형 원칙
            - 원래 문항과 같은 개념·같은 유형·같은 짜임(발문, 그림, <보기>, 선택지 구성)을 지킨다. 학생이 낯설지 않게.
            - 과학적으로 정확해야 한다. 바꾼 숫자로 직접 풀어 보고, 정답이 하나뿐인지(선택형), 오답 선택지가 그럴듯하지만 분명히 틀린지 확인하라.
            - <보기> 문항이면 ㄱ, ㄴ, ㄷ의 참·거짓 조합을 원래와 다르게 해서 정답 번호가 바뀌게 하라.
            - 배점은 원래 문항에 적혀 있으면 그대로, 없으면 선택형 4점, 서술형 5점.
            - 해설(explanation)은 학생이 읽고 이해할 수 있게 풀이 과정을 적는다.

            ## 그림 (매우 중요 — 선생님 규칙)
            학교 시험 그림은 교육용이다. 학생이 헷갈리지 않도록 **원본 그림과 최대한 똑같이** 만든다.
            - 그림을 바꿀 필요가 없으면(숫자·표시가 그대로면) 원본 그림을 그대로 쓴다: 그림 파일이 있으면 복사하고,
              자료가 사진·캡처·PDF 쪽이면 그 그림 부분만 잘라 낸다(PowerShell + System.Drawing으로 잘라 PNG 저장).
            - 바꿔야 하면 원본을 따라 다시 그린다: 같은 배치·모양·색·선 굵기·글꼴 느낌·이름표 위치. 바꾸는 것은 변형에 필요한 숫자·표시뿐.
              원본에 없는 것을 꾸며 넣거나(입체감, 받침대, 흑백 처리 등) 모양을 '개선'하지 마라.
              그린 뒤에는 원본(같은 숫자로 그린 것)과 나란히 놓은 비교 그림을 만들어 직접 보고, 다른 곳을 고쳐라. 2~3번 반복해도 좋다.
            - 그리는 방법: PowerShell 스크립트(.ps1, System.Drawing)를 `{{AuthoringJob.FiguresDir}}/` 에 만들고
              `powershell -NoProfile -ExecutionPolicy Bypass -File 스크립트.ps1` 로 실행한다.
              스크립트 파일은 반드시 UTF-8 **BOM 있는** 인코딩으로 저장하라(Windows PowerShell 5.1은 BOM이 없으면 한글이 깨진다).
              그림은 원본 해상도의 2~3배 크기로, 흰 배경 PNG로 저장한다.
            - 그림 파일 이름: `{{AuthoringJob.FiguresDir}}/{{q.Id}}_원본.png`, `{{AuthoringJob.FiguresDir}}/{{q.Id}}_변형1.png` …
              (여러 변형이 같은 그림을 쓰면 같은 파일을 가리켜도 된다.) 그림 표시 폭 widthMm은 원본 문항에서의 비율을 보고 정한다(한 단 폭 약 100mm).

            ## 결과 파일
            `{{Slash(q.ItemFile)}}` 에 UTF-8 JSON으로 저장하라. 문항 {{q.ExpectedCount}}개를 순서대로:
            {{Format}}

            저장한 뒤 JSON이 올바른지(문법, 정답 번호 범위, 그림 파일이 실제로 있는지) 다시 확인하라.
            결과 파일과 그림 말고는 아무것도 바꾸지 마라. 끝나면 무엇을 만들었는지 두세 줄로 답하라.
            """);
        return sb.ToString();
    }

    /// <summary>검사에서 걸린 것을 고쳐 달라고 할 때(같은 대화를 이어서).</summary>
    public static string Fix(FoundQuestion q, IEnumerable<string> problems) =>
        $"""
        `{Slash(q.ItemFile)}`를 검사했더니 고칠 것이 있다. 고쳐서 같은 파일에 다시 저장하라.
        {string.Join("\n", problems.Select(p => "- " + p))}
        """;

    static string StyleHint(string style) => style switch
    {
        VariantStyle.Numbers => " — 상황·그림 구성은 그대로 두고 수치만 바꾼다(그림 속 숫자도 함께).",
        VariantStyle.Situation => " — 같은 개념을 다른 상황·소재·예시로 묻는다.",
        VariantStyle.Harder => " — 같은 개념에서 한 단계 더 생각해야 풀리게.",
        VariantStyle.Easier => " — 같은 개념을 더 직접적으로 묻게.",
        _ => " — 원래 문항에 맞게 수치 또는 상황을 적절히 바꾼다.",
    };

    static string Slash(string path) => path.Replace('\\', '/');

    /// <summary>문항 JSON 형식(앱의 Question 모델). README의 문항 JSON과 같다.</summary>
    public const string Format = """
        ```jsonc
        {"questions": [
          {
            "type": "MultipleChoice",                       // 선택형. 서술형은 "Essay", 단답형은 "ShortAnswer"
            "stem": "그림 (가)는 … 나타낸 것이다.",          // 발문(문항 번호는 쓰지 않는다)
            "image": { "path": "figures/q01_변형1.png", "widthMm": 80 },   // 그림(없으면 빼기). 경로는 작업 폴더 기준
            "passage": { "title": "", "lines": ["[실험 과정]", "(가) …"] }, // 자료 상자(없으면 빼기)
            "stemAfter": "이에 대한 설명으로 옳은 것만을 <보기>에서 있는 대로 고른 것은?", // 그림·자료 뒤 발문(없으면 빼기)
            "points": 4,
            "box": { "lines": ["A의 온도는 시간이 지날수록 낮아진다.", "…"] }, // <보기>. ㄱ. ㄴ. 은 쓰지 않는다(자동)
            "choices": ["ㄱ", "ㄴ", "ㄱ, ㄴ", "ㄴ, ㄷ", "ㄱ, ㄴ, ㄷ"],     // ①② 기호는 쓰지 않는다(자동)
            "answer": [3],                                  // 정답 번호(1부터)
            "explanation": "풀이"
          },
          {
            "type": "Essay",
            "stem": "… 까닭을 <조건>에 맞춰 서술하시오.",
            "points": 5,
            "conditions": { "title": "<조 건>", "lines": ["∘ '열평형'이라는 낱말을 포함할 것"] },
            "answerText": "모범 답안", "answerLines": 2,
            "subQuestions": [ { "text": "소문항", "points": 2, "answerText": "…" } ]   // 소문항이 있을 때만
          }
        ]}
        ```
        글 안 표기: 수식은 한글 수식 문법으로 `$E = {1} over {2} mv^2$`(단위·간단한 숫자는 그냥 글자로 "6 N"),
        굵게 `**않는**`, 밑줄 `__㉠__`. 글자 그대로의 $ * _ 는 앞에 \ 를 붙인다.
        """;
}
