using System.Text;
using TestMaker.Hwp;

namespace TestMaker.Tests.Hwp;

/// <summary>한글 없이 도는 파일 머리 검사. 암호 문서는 숨김 한글에서 입력창을 띄워 멈추므로 미리 걸러야 한다.</summary>
public sealed class HwpFileInfoTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("testmaker_fileinfo_").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    string FakeHwp(uint properties)
    {
        // OLE 앞부분 흉내 + FileHeader: 서명 32바이트, 버전 4바이트, 속성 4바이트
        var bytes = new byte[600];
        var sig = Encoding.ASCII.GetBytes("HWP Document File");
        sig.CopyTo(bytes, 512);
        BitConverter.GetBytes(0x05000300u).CopyTo(bytes, 512 + 32);
        BitConverter.GetBytes(properties).CopyTo(bytes, 512 + 36);
        var path = Path.Combine(dir, $"p{properties}.hwp");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Theory]
    [InlineData(0x1u, false)] // 압축만
    [InlineData(0x3u, true)]  // 압축 + 암호 (학교 시험지에서 실제로 본 값)
    [InlineData(0x2u, true)]
    [InlineData(0x5u, false)] // 압축 + 배포용
    public void Detects_password_bit_in_hwp_header(uint properties, bool expected)
    {
        Assert.Equal(expected, HwpFileInfo.IsPasswordProtected(FakeHwp(properties)));
    }

    [Fact]
    public void Plain_hwpx_fixture_is_not_protected()
    {
        Assert.False(HwpFileInfo.IsPasswordProtected(Path.Combine(AppContext.BaseDirectory, "Fixtures", "school_exam.hwpx")));
    }
}
