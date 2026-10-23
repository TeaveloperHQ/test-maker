using System.Text;

namespace TestMaker.Ai;

/// <summary>Claude에게 주는 지시문. 결과는 항상 작업 폴더의 파일로 받는다(앱이 읽고 검사한다).</summary>
public static class Prompts
{
    public const string FoundFile = "found.json";

    /// <param name="sources">문항을 찾을 자료(아직 찾지 않은 것만).</param>
    /// <param name="firstId">이번에 찾는 첫 문항의 번호(그림 파일 이름에 쓴다: q{firstId}, q{firstId+1} …).</param>
    public static string FindQuestions(IEnumerable<SourceFile> sources, int firstId = 1)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            너는 한국 중·고등학교 과학 선생님의 시험 출제를 돕는다. 지금 폴더는 작업 폴더다.
            아래 자료 파일을 모두 읽고(그림이면 그림을 보고, PDF면 모든 쪽을), 그 안에 있는 시험 문항을 빠짐없이 찾아
            목록을 만들고, 문항마다 원문을 그대로 옮겨 둔다(원문항).

            자료 파일:
            """);
        var list = sources.ToList();
        foreach (var s in list)
            foreach (var f in s.ReadFiles) sb.AppendLine($"- {Slash(f)}   (원래 파일: {s.Name})");
        foreach (var s in list) AppendPagesAndFigures(sb, s);
        var ids = $"이번에 찾는 문항에 차례로 q{firstId:00}, q{firstId + 1:00}, … 를 붙인다";
        var findRules = OriginalRules(
            "`" + AuthoringJob.FiguresDir + "/q{번호}_원본.png` — " + ids + ".",
            "`" + AuthoringJob.OriginalsDir + "/q{번호}.png` — " + ids + ".");
        sb.AppendLine($$"""

            찾은 문항을 `{{FoundFile}}`에 UTF-8 JSON으로 저장하라. 형식:
            {"questions": [
              {"source": "원래 파일 이름", "number": "07", "summary": "온도가 다른 두 물체를 접촉할 때 열의 이동(보기 고르기)",
               "type": "MultipleChoice", "hasFigure": true,
               "original": { …원문항 하나(아래 문항 형식의 questions 안 항목 하나와 같다)… } }
            ]}
            - 자료에 나온 순서대로. number는 자료에 적힌 번호 그대로(없으면 순서대로 "1", "2" …).
            - type: 선택형 MultipleChoice, 단답형 ShortAnswer, 서술형·논술형 Essay.
            - summary: 선생님이 어떤 문항인지 알아볼 수 있게 한 줄(30자 안팎). 잘려서 일부만 보이면 끝에 "(일부 잘림)".
            - 문항이 아닌 것(표지, 유의사항, 정답표, 해설, 단원 정리)은 넣지 마라. 묶음 문항([3~4] 공통 지문)은 문항마다 따로.
            - 문항을 하나도 못 찾으면 {"questions": []}.

            {{findRules}}

            파일 저장과 그림 말고는 아무것도 바꾸지 마라. 끝나면 찾은 문항 수만 한 줄로 답하라.
            """);
        return sb.ToString();
    }

    public const string OriginalsFile = "originals_new.json";

    /// <summary>이미 찾은 문항의 원문만 나중에 옮길 때(문항 목록은 그대로).</summary>
    public static string TranscribeOriginals(SourceFile source, IEnumerable<FoundQuestion> questions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            너는 한국 중·고등학교 과학 선생님의 시험 출제를 돕는다. 지금 폴더는 작업 폴더다.
            아래 자료에서 다음 문항들을 찾아, 문항마다 원문을 그대로 옮겨라(원문항).

            자료 파일:
            """);
        foreach (var f in source.ReadFiles) sb.AppendLine($"- {Slash(f)}");
        AppendPagesAndFigures(sb, source);
        sb.AppendLine("\n옮길 문항(id: 자료의 번호 · 요약):");
        foreach (var q in questions) sb.AppendLine($"- {q.Id}: {q.Number}번 · {q.Summary}");
        var rules = OriginalRules(
            "`" + AuthoringJob.FiguresDir + "/{id}_원본.png` (예: q01_원본.png).",
            "`" + AuthoringJob.OriginalsDir + "/{id}.png` (예: originals/q01.png).");
        sb.AppendLine($$"""

            `{{OriginalsFile}}`에 UTF-8 JSON으로 저장하라. 형식:
            {"originals": [ {"id": "q01", "original": { …원문항 하나(아래 문항 형식의 questions 안 항목 하나와 같다)… } } ]}

            {{rules}}

            파일 저장과 그림 말고는 아무것도 바꾸지 마라. 끝나면 옮긴 문항 수만 한 줄로 답하라.
            """);
        return sb.ToString();
    }

    static void AppendPagesAndFigures(StringBuilder sb, SourceFile s)
    {
        if (s.Pages.Count > 0)
        {
            sb.AppendLine($"'{s.Name}'의 쪽 그림(PDF 쪽을 그림으로 저장한 것 — 문항 영역과 그림은 여기서 잘라 낸다):");
            foreach (var p in s.Pages) sb.AppendLine($"- {Slash(p)}");
        }
        if (s.Figures.Count > 0)
        {
            sb.AppendLine($"'{s.Name}' 안에 들어 있던 그림 파일(원본 그대로라 화질이 좋다 — 맞는 것이 있으면 이것을 쓴다):");
            foreach (var f in s.Figures) sb.AppendLine($"- {Slash(f)}");
        }
    }

    /// <summary>원문항을 옮기는 규칙(그대로 옮김, 정답은 풀어서, 그림은 원본 그대로, 문항 영역 캡처).</summary>
    static string OriginalRules(string figureName, string snapshotName) => $$"""
        ## 원문 그림(문항 캡처) — 선생님이 원문을 볼 때 쓴다
        - 문항마다 그 문항 영역만(문항 번호부터 마지막 선택지·답란까지, 그 문항의 그림·<보기>·자료 포함) 쪽 그림(또는 그림·캡처 자료)에서
          잘라 {{snapshotName}} 로 저장한다. 다른 문항이 섞이지 않게 위아래 경계를 정확히, 여백은 조금만. 2단 편집이면 그 문항이 있는 단만.
          묶음 문항의 공통 지문은 함께 넣는다. 잘라 낸 그림을 직접 열어 보고, 잘리거나 넘친 곳이 있으면 다시 자른다.

        ## 원문항(original) 옮기기
        - 발문·자료·<보기>·선택지를 **자료에 적힌 그대로** 옮긴다(고치거나 다듬지 마라). 문항 번호와 ①② 기호, ㄱ. ㄴ. 머리는 빼고 적는다.
        - 정답과 해설: 자료에 정답이 있으면 그것을, 없으면 직접 풀어서 넣는다. 배점이 적혀 있으면 그대로, 없으면 선택형 4점·서술형 5점.
        - 그림: 원본 그림을 **그대로** 쓴다(다시 그리지 않는다). 위 그림 파일 중 맞는 것을 복사하거나, 없으면 쪽 그림·캡처 자료에서
          그 그림 부분만 잘라 낸다. 파일 이름은 {{figureName}} 그림을 얻을 수 없으면 "image"를 빼라.
        - 잘라 내기: PowerShell + System.Drawing. 스크립트 .ps1은 UTF-8 BOM으로 저장해
          `powershell -NoProfile -ExecutionPolicy Bypass -File 스크립트.ps1`로 실행한다(여러 문항을 한 스크립트로 잘라도 된다).
        - 문항 형식:
        {{Format}}
        """;

    /// <param name="hasOriginal">원문항을 [문항 찾기] 때 이미 옮겨 두었는지. 그렇다면 변형만 만든다.</param>
    public static string MakeVariants(AuthoringJob job, FoundQuestion q, bool hasOriginal = false)
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
            AppendPagesAndFigures(sb, source);
        }
        sb.AppendLine();
        if (hasOriginal)
            sb.AppendLine($"원래 문항은 `{Slash(q.OriginalFile)}`에 이미 옮겨 두었다(그림 포함). 이것을 기준으로 삼고, 결과 파일에는 **변형 문항만** 넣는다.");
        else if (q.KeepOriginal) sb.AppendLine("- 첫 문항: 원래 문항을 그대로 옮긴 것(글, 그림, 선택지, 정답 모두 원래대로).");
        if (q.Variants > 0)
        {
            sb.AppendLine($"- 변형 문항 {q.Variants}개. 변형 방식: {q.Style}{StyleHint(q.Style)}");
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
            `{{Slash(q.ItemFile)}}` 에 UTF-8 JSON으로 저장하라. 문항 {{(hasOriginal ? q.Variants : q.ExpectedCount)}}개를 순서대로:
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
            "explanation": "풀이",
            "meta": { "unit": "열과 우리 생활 - 열평형", "difficulty": "중" }   // 단원(교과서 단원명), 난이도 상/중/하 — 문항함에서 찾을 때 쓴다
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
        글 안 표기: 굵게 `**않는**`, 밑줄 `__㉠__`. 글자 그대로의 $ * _ 는 앞에 \ 를 붙인다.

        수식(한글 수식 편집기 문법, `$…$`) 규칙 — 시험지에서 그대로 수식 개체가 된다:
        - 분수는 분자·분모를 반드시 중괄호로: `${1} over {4}$`, `${F} over {2}$`, `${3} over {4} F$`.
          (`$3 over 4$F`처럼 분수 뒤에 글자를 수식 밖에 붙이지 마라 — 분모인지 아닌지 헷갈린다.)
        - 변수(W, F, m, v …)가 들어간 식은 **식 전체를 하나의 수식으로**: `$W - {F} over {2} = 9$ N`.
          글자 W와 수식을 섞어 `W − $F over 2$` 처럼 쓰지 마라(글꼴이 섞인다). 수식 바로 앞뒤에 영문자·숫자를 붙이지 마라.
        - 곱하기 `TIMES`, 나누기 `DIV`, 위첨자 `v^2`, 아래첨자 `F_1`, 제곱근 `sqrt {2}`.
        - 단위는 수식 밖에 글자로 둔다("6 N", "$W = 12$ N"). 분수·변수가 없는 숫자는 수식으로 만들지 말고 글자로("3 N").
        """;
}
