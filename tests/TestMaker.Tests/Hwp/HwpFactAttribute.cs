using System.Diagnostics;
using TestMaker.Hwp;

namespace TestMaker.Tests.Hwp;

/// <summary>
/// 한글을 실제로 띄우는 통합 테스트. 한글이 없거나, 사용자가 한글을 열어 두었으면(작업 보호) 건너뛴다.
/// 같은 컬렉션("Hwp")에 넣어 한 번에 하나씩 돌린다.
/// </summary>
public sealed class HwpFactAttribute : FactAttribute
{
    public HwpFactAttribute()
    {
        if (!HwpSession.IsInstalled) Skip = "한글이 설치되어 있지 않습니다.";
        else if (Process.GetProcessesByName("Hwp").Length > 0) Skip = "한글 창이 열려 있어 건너뜁니다(작업 중인 문서 보호).";
    }
}

[CollectionDefinition("Hwp", DisableParallelization = true)]
public sealed class HwpCollection;
