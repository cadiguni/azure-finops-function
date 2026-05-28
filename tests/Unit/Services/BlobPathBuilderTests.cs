using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Services;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class BlobPathBuilderTests
{
    [Fact]
    public void BuildAnalysisPath_ShouldReturnExpectedPath()
    {
        var date = new DateTime(2026, 2, 24);

        var path = BlobPathBuilder.BuildAnalysisPath(date, "sub-001", "recommendations.json");

        path.Should().Be("analyses/year=2026/month=02/day=24/sub-001/recommendations.json");
    }

    [Fact]
    public void BuildSummaryPath_ShouldReturnExpectedPath()
    {
        var date = new DateTime(2026, 1, 5);

        var path = BlobPathBuilder.BuildSummaryPath(date, "sub-xyz", "summary.json");

        path.Should().Be("summaries/year=2026/month=01/day=05/sub-xyz/summary.json");
    }

    [Fact]
    public void BuildDailyPaths_ShouldReturnExpectedValues()
    {
        var date = new DateTime(2026, 12, 3);

        BlobPathBuilder.BuildAnalysesDailyPrefix(date)
            .Should().Be("analyses/year=2026/month=12/day=03/");
        BlobPathBuilder.BuildDailySummaryPath(date)
            .Should().Be("summaries/year=2026/month=12/day=03/summary.json");
        BlobPathBuilder.BuildDailyTop10Path(date)
            .Should().Be("summaries/year=2026/month=12/day=03/top10.json");
    }

    [Fact]
    public void FileNames_WithAnalysisId_ShouldReturnExpectedName()
    {
        var fileName = BlobPathBuilder.FileNames.WithAnalysisId("analysis-123");

        fileName.Should().Be("analysisId=analysis-123.json");
    }
}
