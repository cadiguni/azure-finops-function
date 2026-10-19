using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Services;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public sealed class CostExportManifestParserTests
{
    [Fact]
    public void Parse_ShouldDeserializeRealManifestCaseInsensitively()
    {
        var manifest = new CostExportManifestParser().Parse(RealManifestJson, "manifest.json");

        manifest.ManifestVersion.Should().Be("2024-04-01");
        manifest.ExportConfig.ExportName.Should().Be("finops-actual-cost-current-billing");
        manifest.ExportConfig.Type.Should().Be("Usage");
        manifest.ExportConfig.TimeFrame.Should().Be("MonthToDate");
        manifest.DeliveryConfig.RootFolderPath.Should().Be(
            "subscriptions/00000000-0000-0000-0000-00000000a006/billing-period/current");
        manifest.RunInfo.RunId.Should().Be("aae7057f-0000-0000-0000-000000000000");
        manifest.Blobs.Should().HaveCount(2);
        manifest.Blobs.Sum(blob => blob.DataRowCount).Should().Be(2298);
    }

    [Theory]
    [InlineData("runInfo", """{"submittedTime":"2026-08-19T19:30:53Z"}""", "runInfo.runId")]
    [InlineData("runInfo", """{"runId":"run-1"}""", "runInfo.submittedTime")]
    [InlineData("exportConfig", "{}", "exportConfig.exportName")]
    [InlineData("deliveryConfig", "{}", "deliveryConfig.rootFolderPath")]
    public void Parse_WhenRequiredPropertyIsMissing_ShouldReject(
        string section,
        string replacement,
        string expectedProperty)
    {
        var json = section switch
        {
            "runInfo" => MinimalManifestJson.Replace(
                "\"runInfo\":{\"runId\":\"run-1\",\"submittedTime\":\"2026-08-19T19:30:53Z\"}",
                $"\"runInfo\":{replacement}"),
            "exportConfig" => MinimalManifestJson.Replace(
                "\"exportConfig\":{\"exportName\":\"finops-actual-cost-current-billing\"}",
                $"\"exportConfig\":{replacement}"),
            _ => MinimalManifestJson.Replace(
                "\"deliveryConfig\":{\"rootFolderPath\":\"subscriptions/00000000-0000-0000-0000-00000000a006/billing-period/current\"}",
                $"\"deliveryConfig\":{replacement}")
        };

        var act = () => new CostExportManifestParser().Parse(json, "manifest.json");

        act.Should().Throw<InvalidDataException>()
            .WithMessage($"*{expectedProperty}*");
    }

    private const string MinimalManifestJson = """
    {
      "exportConfig":{"exportName":"finops-actual-cost-current-billing"},
      "deliveryConfig":{"rootFolderPath":"subscriptions/00000000-0000-0000-0000-00000000a006/billing-period/current"},
      "runInfo":{"runId":"run-1","submittedTime":"2026-08-19T19:30:53Z"},
      "blobs":[{"blobName":"part.csv"}]
    }
    """;

    private const string RealManifestJson = """
    {
      "MANIFESTVERSION": "2024-04-01",
      "byteCount": 1931219,
      "blobCount": 2,
      "dataRowCount": 2298,
      "exportConfig": {
        "exportName": "finops-actual-cost-current-billing",
        "resourceId": "/subscriptions/00000000-0000-0000-0000-00000000a006/providers/Microsoft.CostManagement/exports/finops-actual-cost-current-billing",
        "dataVersion": "2019-11-01",
        "apiVersion": "2025-03-01",
        "type": "Usage",
        "timeFrame": "MonthToDate",
        "granularity": "Daily"
      },
      "deliveryConfig": {
        "partitionData": true,
        "dataOverwriteBehavior": "OverwritePreviousReport",
        "fileFormat": "Csv",
        "compressionMode": "None",
        "rootFolderPath": "subscriptions/00000000-0000-0000-0000-00000000a006/billing-period/current"
      },
      "runInfo": {
        "executionType": "OnDemand",
        "submittedTime": "2026-08-19T19:30:53.3331905Z",
        "runId": "aae7057f-0000-0000-0000-000000000000",
        "startDate": "2026-08-01T00:00:00",
        "endDate": "2026-08-19T00:00:00Z"
      },
      "blobs": [
        {
          "blobName": "subscriptions/00000000-0000-0000-0000-00000000a006/billing-period/current/finops-actual-cost-current-billing/20260801-20260831/aae7057f-0000-0000-0000-000000000000/part_0_0001.csv",
          "byteCount": 100,
          "dataRowCount": 1200
        },
        {
          "blobName": "subscriptions/00000000-0000-0000-0000-00000000a006/billing-period/current/finops-actual-cost-current-billing/20260801-20260831/aae7057f-0000-0000-0000-000000000000/part_0_0002.csv",
          "byteCount": 100,
          "dataRowCount": 1098
        }
      ]
    }
    """;
}
