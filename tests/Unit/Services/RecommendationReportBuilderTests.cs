using System.Globalization;
using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class RecommendationReportBuilderTests
{
    [Fact]
    public void CsvReport_ShouldExposeObservedCostProvenanceAndVersionedPeriods()
    {
        var report = CreateReport();

        var csv = new CsvReportBuilder(NullLogger<CsvReportBuilder>.Instance).BuildReport(report);

        csv.Should().Contain("RecordType,ManagementGroup");
        csv.Should().Contain("ObservedCost,CostCurrency,CostScope,CostSource,CostBasis,CostDataAsOf,CostPeriodFrom,CostPeriodTo,DatasetRevision,SnapshotRevision,ReportPeriodFrom,ReportPeriodTo,FinancialTotalCost");
        csv.Should().Contain("SnapshotSummary");
        csv.Should().Contain("RecommendationObservedCostTotal,SavingsStatus");
        csv.Should().Contain("310.00,420.00,BRL,Partial,Provisional,2026-08-31,CostManagementExport");
        csv.Should().Contain("42.50,BRL,Shared:plan-1,CostManagementExport,ActualCost");
        csv.Should().Contain("2026-08-01,2026-08-31,dataset-7,snapshot-9,2026-08-01,2026-08-31");
    }

    [Fact]
    public void HtmlReport_ShouldDistinguishObservedCostFromMonthlyProjection()
    {
        var report = CreateReport();

        var html = new HtmlReportBuilder(NullLogger<HtmlReportBuilder>.Instance).BuildReport(report);

        html.Should().Contain("Período financeiro:");
        html.Should().Contain("snapshot-9");
        html.Should().Contain("Custo Observado");
        html.Should().Contain("Projeção Mensal");
        html.Should().Contain("Custos observados");
        html.Should().Contain("Custo observado associado");
        html.Should().Contain("BRL 310,00");
        html.Should().Contain("CostManagementExport");
        html.Should().Contain("Compartilhado: plan-1");
        html.Should().Contain("class=\"recommendation-description-row\"");
        html.Should().Contain("<td colspan=\"8\">");
        html.Should().Contain("Descrição:</strong> Review");
        html.Should().Contain("Parcial");
        html.Should().NotContain("Relatório de anomalias não disponível");
    }

    [Fact]
    public void HtmlReport_ShouldUseBrazilianNumberFormattingRegardlessOfHostCulture()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            var report = CreateReport();
            report.FinancialSummary!.TotalCost = 1234.56m;

            var html = new HtmlReportBuilder(NullLogger<HtmlReportBuilder>.Instance).BuildReport(report);

            html.Should().Contain("BRL 1.234,56");
            html.Should().NotContain("BRL 1,234.56");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public void HtmlReport_ShouldUseProvenancePeriodAndExposeCostMatchEvidence()
    {
        var report = CreateReport();
        report.Summary.ObservedCostTotal = null;
        var recommendation = report.ManagementGroups.Single().Subscriptions.Single()
            .ResourceGroups.Single().Recommendations.Single();
        recommendation.CostPeriodFrom = null;
        recommendation.CostPeriodTo = null;
        recommendation.CostScope = "Approximate";
        recommendation.CostNote = "Associado por nome, grupo e tipo.";
        recommendation.CostMatchedResourceId =
            "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Web/serverFarms/plan";
        recommendation.CostProvenance = new CostReadProvenance
        {
            Source = "CostManagementExport",
            RequestedFrom = new DateTime(2026, 7, 1),
            RequestedTo = new DateTime(2026, 7, 31)
        };

        var html = new HtmlReportBuilder(NullLogger<HtmlReportBuilder>.Instance).BuildReport(report);

        html.Should().Contain("não agregável com confiança");
        html.Should().Contain("Aproximado | 01/07/2026 a 31/07/2026");
        html.Should().Contain("Qualidade do custo:</strong> Associado por nome, grupo e tipo.");
        html.Should().Contain("Recurso cobrado:</strong>");
        html.Should().Contain(recommendation.CostMatchedResourceId);
    }

    [Fact]
    public void BuildSnapshot_WithMixedCurrencies_ShouldKeepPerResourceAmountsAndTotalOneCurrency()
    {
        var summary = new DashboardSummaryDocument
        {
            Date = "2026-08-31",
            ScopeKey = "global",
            SnapshotRevision = "mixed-snapshot",
            PotentialMonthlySavings = 0m,
            FinancialSummary = new CostReportFinancialSummary { Availability = "MixedCurrency" }
        };
        var brl = Recommendation("resource-brl", "BRL", 30m);
        var usd = Recommendation("resource-usd", "USD", 20m);

        var report = RecommendationReportService.BuildSnapshot(summary,
            [(brl, RecommendationStatuses.New, true), (usd, RecommendationStatuses.New, true)]);

        var recommendations = report.ManagementGroups.SelectMany(group => group.Subscriptions)
            .SelectMany(subscription => subscription.ResourceGroups)
            .SelectMany(group => group.Recommendations).ToList();

        recommendations.Single(item => item.Currency == "BRL").PotentialSavings.Should().Be(30m);
        recommendations.Single(item => item.Currency == "USD").PotentialSavings.Should().Be(20m);
        recommendations.Should().OnlyContain(item => item.SavingsStatus == "Quantified");
        // Never BRL + USD: the scalar total uses a single currency.
        report.Summary.SavingsCurrency.Should().Be("BRL");
        report.Summary.TotalPotentialSavings.Should().Be(30m);
    }

    [Fact]
    public void BuildSnapshot_WhenFinancialCurrencyHasNoSavings_ShouldStillQuantifyRecommendations()
    {
        // Regression: the anomaly/export currency used to gate every recommendation to zero.
        var summary = new DashboardSummaryDocument
        {
            Date = "2026-10-14",
            ScopeKey = "global",
            SnapshotRevision = "usd-financial-snapshot",
            PotentialMonthlySavings = 0m,
            FinancialSummary = new CostReportFinancialSummary { Availability = "Partial", Currency = "USD" }
        };
        var first = Recommendation("resource-a", "BRL", 30m);
        var second = Recommendation("resource-b", "BRL", 45m);

        var report = RecommendationReportService.BuildSnapshot(summary,
            [(first, RecommendationStatuses.New, true), (second, RecommendationStatuses.New, true)]);
        var recommendations = report.ManagementGroups.SelectMany(group => group.Subscriptions)
            .SelectMany(subscription => subscription.ResourceGroups)
            .SelectMany(group => group.Recommendations).ToList();

        recommendations.Sum(item => item.PotentialSavings).Should().Be(75m);
        recommendations.Should().OnlyContain(item => item.SavingsStatus == "Quantified");
        report.Summary.SavingsCurrency.Should().Be("BRL");
        report.Summary.TotalPotentialSavings.Should().Be(75m);
    }

    [Fact]
    public void BuildSnapshot_InactiveRecommendations_ShouldNotBeQuantified()
    {
        var summary = new DashboardSummaryDocument
        {
            Date = "2026-10-14", ScopeKey = "global", SnapshotRevision = "inactive-snapshot",
            FinancialSummary = new CostReportFinancialSummary { Currency = "BRL" }
        };
        var resolved = Recommendation("resource-a", "BRL", 30m);

        var report = RecommendationReportService.BuildSnapshot(summary,
            [(resolved, RecommendationStatuses.Resolved, false)]);

        report.Summary.TotalPotentialSavings.Should().Be(0m);
    }

    [Fact]
    public void BuildSnapshot_ShouldTotalOnlyTheSnapshotCurrency()
    {
        var summary = new DashboardSummaryDocument
        {
            Date = "2026-08-31",
            ScopeKey = "global",
            SnapshotRevision = "brl-snapshot",
            PotentialMonthlySavings = 30m,
            FinancialSummary = new CostReportFinancialSummary
            {
                Availability = "Available",
                Currency = "BRL"
            }
        };
        var brl = Recommendation("resource-brl", "BRL", 30m);
        var usd = Recommendation("resource-usd", "USD", 20m);

        var report = RecommendationReportService.BuildSnapshot(summary,
            [(brl, RecommendationStatuses.New, true), (usd, RecommendationStatuses.New, true)]);
        var recommendations = report.ManagementGroups.SelectMany(group => group.Subscriptions)
            .SelectMany(subscription => subscription.ResourceGroups)
            .SelectMany(group => group.Recommendations).ToList();

        recommendations.Single(item => item.Currency == "BRL").PotentialSavings.Should().Be(30m);
        recommendations.Single(item => item.Currency == "USD").PotentialSavings.Should().Be(20m);
        report.Summary.SavingsCurrency.Should().Be("BRL");
        recommendations.Where(item => item.Currency == "BRL").Sum(item => item.PotentialSavings)
            .Should().Be(report.Summary.TotalPotentialSavings);
    }

    private static CostRecommendation Recommendation(string name, string currency, decimal savings)
        => new()
        {
            Type = "Review",
            ResourceId = $"/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Sql/servers/s/databases/{name}",
            ResourceName = name,
            ResourceType = "Microsoft.Sql/servers/databases",
            ResourceGroup = "rg",
            SubscriptionId = "sub",
            ObservedCost = 100m,
            EstimatedMonthlyCost = 100m,
            EstimatedMonthlySavings = savings,
            CostCurrency = currency,
            CostSource = CostMatchSources.ResourceId
        };

    private static RecommendationReport CreateReport()
        => new()
        {
            SnapshotRevision = "snapshot-9",
            PeriodFrom = "2026-08-01",
            PeriodTo = "2026-08-31",
            Currency = "BRL",
            AnalysisDate = new DateTime(2026, 8, 31),
            GeneratedAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
            FinancialSummary = new CostReportFinancialSummary
            {
                TotalCost = 310m,
                MonthlyProjection = 420m,
                Currency = "BRL",
                Availability = "Partial",
                Finality = "Provisional",
                LatestDataAsOf = "2026-08-31",
                AvailableSubscriptions = 1,
                ExpectedSubscriptions = 2,
                DailyRevisions = new Dictionary<string, string> { ["2026-08-31"] = "dataset-7" },
                Sources = ["CostManagementExport"]
            },
            Summary = new ReportSummary
            {
                TotalRecommendations = 1,
                TotalPotentialSavings = 15m,
                ObservedCostTotal = 42.5m
            },
            ManagementGroups =
            {
                new ManagementGroupReport
                {
                    Id = "mg",
                    Name = "MG",
                    Subscriptions =
                    {
                        new SubscriptionReport
                        {
                            Id = "sub",
                            Name = "Subscription",
                            ResourceGroups =
                            {
                                new ResourceGroupReport
                                {
                                    Name = "rg",
                                    Recommendations =
                                    {
                                        new ActionableRecommendation
                                        {
                                            ResourceName = "plan",
                                            ResourceType = "Microsoft.Web/serverfarms",
                                            Action = "Investigar",
                                            Priority = "Medium",
                                            Description = "Review",
                                            ObservedCost = 42.5m,
                                            CurrentCost = 90m,
                                            PotentialSavings = 15m,
                                            SavingsStatus = "Quantified",
                                            Currency = "BRL",
                                            CostScope = "Shared:plan-1",
                                            CostPeriodFrom = new DateTime(2026, 8, 1),
                                            CostPeriodTo = new DateTime(2026, 8, 31),
                                            CostProvenance = new CostReadProvenance
                                            {
                                                Source = "CostManagementExport",
                                                CostBasis = "ActualCost",
                                                DatasetRevision = "dataset-7",
                                                DataAsOf = new DateTime(2026, 8, 31)
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };
}
