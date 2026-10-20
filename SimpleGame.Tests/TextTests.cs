namespace SimpleGame.Tests;

public sealed class TextTests
{
    [Theory]
    [InlineData("žluťoučký", "ZLUTOUCKY")]
    [InlineData("Ř", "R")]
    [InlineData("e\u0301", "E")]
    [InlineData("abc", "ABC")]
    public void NormalizesCzechLetters(string input, string expected) => Assert.Equal(expected, CzechText.Letters(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("A B")]
    [InlineData("A1")]
    [InlineData("<b>")]
    [InlineData("\u0301")]
    [InlineData("\ud800")]
    public void InvalidLettersAre400(string? input)
    {
        var error = Assert.Throws<GameException>(() => CzechText.Letters(input));
        Assert.Equal(400, error.StatusCode);
        Assert.Equal("invalid_input", error.Code);
    }

    [Fact]
    public void InputLengthIsLimitedBeforeRemovingMarks() =>
        Assert.Throws<GameException>(() => CzechText.Letters("a" + new string('\u0301', 64)));
}
