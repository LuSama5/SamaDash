using SamaDash.Services;

namespace SamaDash.Tests;

public class CalculatorTests
{
    [Theory]
    [InlineData("15*3", 45)]
    [InlineData("2+3*4", 14)]
    [InlineData("(2+3)*4", 20)]
    [InlineData("2^3^2", 512)]
    [InlineData("-2^2", -4)]
    [InlineData("1,5+2", 3.5)]
    [InlineData("1.5 + 2", 3.5)]
    [InlineData("200*10%", 20)]
    [InlineData("50%", 0.5)]
    [InlineData("10 / 4", 2.5)]
    [InlineData("3x4", 12)]
    [InlineData("8÷2", 4)]
    public void Evaluates(string input, double expected)
    {
        Assert.True(Calculator.TryEvaluate(input, out var result));
        Assert.Equal(expected, result, 9);
    }

    [Theory]
    [InlineData("2024")]          // numero da solo
    [InlineData("2024-10-05")]    // data in un nome di file
    [InlineData("1/0")]           // divisione per zero
    [InlineData("(2+3")]          // parentesi non chiusa
    [InlineData("1.2.3+1")]       // numero non valido
    [InlineData("report 2+2")]    // testo
    [InlineData("")]
    public void RejectsNonExpressions(string input)
    {
        Assert.False(Calculator.TryEvaluate(input, out _));
    }

    [Theory]
    [InlineData("10 km in mi", "6,2137 mi")]
    [InlineData("100 c in f", "212 f")]
    [InlineData("32 °F in °C", "0 °C")]
    [InlineData("1 gb in mb", "1.024 mb")]
    [InlineData("2,5 kg to lb", "5,5116 lb")]
    public void ConvertsUnits(string input, string expected)
    {
        Assert.True(Calculator.TryConvertUnits(input, out var result));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("10 km in kg")]   // gruppi diversi
    [InlineData("20 eur in usd")] // valute: le gestisce Currency
    [InlineData("km in mi")]
    public void RejectsInvalidConversions(string input)
    {
        Assert.False(Calculator.TryConvertUnits(input, out _));
    }

    [Fact]
    public void FormatsItalian()
    {
        Assert.Equal("1.234,5", Calculator.Format(1234.5));
    }
}
