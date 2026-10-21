using TestMaker.Core.Text;

namespace TestMaker.Tests.Core;

public sealed class InlineMarkupTests
{
    [Fact]
    public void Plain_text_is_one_segment()
    {
        var s = InlineMarkup.Parse("다음 중 옳은 것은?");
        Assert.Equal([new Segment(SegmentKind.Text, "다음 중 옳은 것은?")], s);
    }

    [Fact]
    public void Equation_bold_and_underline_are_split()
    {
        var s = InlineMarkup.Parse("질량 $m$인 **옳은** __㉠__");
        Assert.Equal(
        [
            new Segment(SegmentKind.Text, "질량 "),
            new Segment(SegmentKind.Equation, "m"),
            new Segment(SegmentKind.Text, "인 "),
            new Segment(SegmentKind.Text, "옳은", Bold: true),
            new Segment(SegmentKind.Text, " "),
            new Segment(SegmentKind.Text, "㉠", Underline: true),
        ], s);
    }

    [Fact]
    public void Escapes_produce_literal_characters()
    {
        var s = InlineMarkup.Parse(@"가격 \$5, 별표 \*\*, 밑줄 \_\_");
        Assert.Equal("가격 $5, 별표 **, 밑줄 __", Assert.Single(s).Text);
    }

    [Fact]
    public void Single_star_or_underscore_is_text()
    {
        Assert.Equal("a*b_c", Assert.Single(InlineMarkup.Parse("a*b_c")).Text);
    }

    [Theory]
    [InlineData("$x^2")]
    [InlineData("**굵게")]
    [InlineData("__밑줄")]
    public void Unclosed_markup_throws(string source)
    {
        Assert.Throws<FormatException>(() => InlineMarkup.Parse(source));
    }

    /// <summary>실제로 나온 표기: 분수 뒤에 변수를 수식 밖에 붙이거나, 식을 글자와 수식으로 쪼갬.</summary>
    [Theory]
    [InlineData("$3 over 4$F = 6 N", "3 over 4")]
    [InlineData("W − $F over 2$ = 9 N", null)]          // 띄어 쓰면 붙은 것은 아니다(글꼴은 섞이지만 읽기는 됨)
    [InlineData("W$-F$", "-F")]
    [InlineData("부피의 ${1} over {4}$만", null)]       // 한글이 붙는 것은 괜찮다(조사)
    public void Finds_letters_glued_to_equations(string source, string? glued)
    {
        var found = InlineMarkup.GluedEquations(source).ToList();
        if (glued is null) Assert.Empty(found);
        else Assert.Equal([glued], found);
    }

    [Theory]
    [InlineData("부피의 $1 over 4$만", "부피의 1/4만")]
    [InlineData("${3} over {4} F$", "3/4 F")]
    [InlineData("$W - {F} over {2} = 9$ N", "W - F/2 = 9 N")]
    [InlineData("$E = {1} over {2} mv^2$", "E = 1/2 mv²")]
    [InlineData("${a+b} over {2}$", "(a+b)/2")]
    [InlineData("$2 TIMES 3$", "2 × 3")]
    public void Plain_preview_reads_equations(string source, string plain)
    {
        Assert.Equal(plain, InlineMarkup.ToPlain(source));
    }

    [Fact]
    public void Width_counts_hangul_full_and_ascii_half()
    {
        Assert.Equal(2.0, InlineMarkup.EstimateWidthEm("가나"));
        Assert.Equal(1.0, InlineMarkup.EstimateWidthEm("ab"));
        Assert.Equal(3.0, InlineMarkup.EstimateWidthEm("ㄱ, ㄷ")); // ㄱ 1 + 쉼표 0.5 + 공백 0.5 + ㄷ 1
        Assert.Equal(3.0, InlineMarkup.EstimateWidthEm("$F over m$")); // 수식: 영문자 F,o,v,e,r,m 6개 × 0.5 (넉넉한 어림)
    }
}
