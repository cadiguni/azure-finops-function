using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Services;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class CostTagParserTests
{
    [Fact]
    public void TryGetTagValue_ShouldReadJsonObjectFormat()
    {
        CostTagParser.TryGetTagValue("""{"CentrodeCusto":"NAP","env":"prod"}""", "CentrodeCusto")
            .Should().Be("NAP");
    }

    [Fact]
    public void TryGetTagValue_ShouldReadPairsWithoutOuterBraces()
    {
        // Exports mais antigos entregam só os pares internos, sem as chaves externas.
        CostTagParser.TryGetTagValue("\"CentrodeCusto\": \"Digital\", \"owner\": \"ti\"", "CentrodeCusto")
            .Should().Be("Digital");
    }

    [Fact]
    public void TryGetTagValue_ShouldIgnoreCaseOnTagName()
    {
        CostTagParser.TryGetTagValue("""{"centrodecusto":"Centris"}""", "CentrodeCusto")
            .Should().Be("Centris");
    }

    [Fact]
    public void TryGetTagValue_ShouldTrimSurroundingWhitespace()
    {
        CostTagParser.TryGetTagValue("""{"CentrodeCusto":"  NAP  "}""", "CentrodeCusto")
            .Should().Be("NAP");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryGetTagValue_ShouldReturnNullWhenThereAreNoTags(string? rawTags)
    {
        CostTagParser.TryGetTagValue(rawTags, "CentrodeCusto").Should().BeNull();
    }

    [Fact]
    public void TryGetTagValue_ShouldReturnNullWhenTagIsAbsent()
    {
        CostTagParser.TryGetTagValue("""{"env":"prod","owner":"ti"}""", "CentrodeCusto")
            .Should().BeNull();
    }

    [Fact]
    public void TryGetTagValue_ShouldTreatEmptyValueAsMissing()
    {
        // Tag presente porém vazia não atribui o custo a ninguém.
        CostTagParser.TryGetTagValue("""{"CentrodeCusto":"   "}""", "CentrodeCusto").Should().BeNull();
    }

    [Fact]
    public void TryGetTagValue_ShouldNotThrowOnMalformedContent()
    {
        CostTagParser.TryGetTagValue("isto não é json", "CentrodeCusto").Should().BeNull();
    }

    [Fact]
    public void ParseTags_ShouldRecoverPairsFromMalformedJson()
    {
        var tags = CostTagParser.ParseTags("""{"CentrodeCusto": "NAP", "quebrado}""");

        tags.Should().Contain(pair => pair.Key == "CentrodeCusto" && pair.Value == "NAP");
    }

    [Fact]
    public void ParseTags_ShouldReadNonStringValues()
    {
        CostTagParser.ParseTags("""{"custo":123}""")
            .Should().ContainSingle(pair => pair.Key == "custo" && pair.Value == "123");
    }
}
