using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Services;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class CostExportCsvParserTests
{
    [Fact]
    public void Parse_ShouldDeriveResourceGroupAndTypeFromResourceIdWhenExportColumnsAreEmpty()
    {
        const string csv = "Date,SubscriptionId,ServiceName,ResourceId,PreTaxCost,Currency\n2026-08-12,sub-1,Azure SQL,/subscriptions/sub-1/resourceGroups/rg-data/providers/Microsoft.Sql/servers/sql/databases/db,12.34,BRL\n";

        var record = new CostExportCsvParser().Parse(new StringReader(csv), "sub-1").Single();

        record.ResourceGroup.Should().Be("rg-data");
        record.ResourceType.Should().Be("Microsoft.Sql/servers/databases");
    }

    [Fact]
    public void Parse_ShouldReadPayAsYouGoColumnsAndQuotedValues()
    {
        const string csv = """
        ﻿Date,SubscriptionId,ServiceName,InstanceId,PreTaxCost,Currency
        2026-08-12,sub-1,"Azure Storage, Standard","/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/stg",12.34,BRL
        """;

        var records = new CostExportCsvParser().Parse(new StringReader(csv), "sub-1").ToList();

        records.Should().ContainSingle();
        records[0].SubscriptionId.Should().Be("sub-1");
        records[0].UsageDate.Should().Be(new DateTime(2026, 8, 12));
        records[0].ServiceName.Should().Be("Azure Storage, Standard");
        records[0].Cost.Should().Be(12.34m);
        records[0].Currency.Should().Be("BRL");
    }

    [Fact]
    public void Parse_ShouldSupportMcaColumnsAndFallbackSubscription()
    {
        const string csv = """
        Date,ConsumedService,ResourceId,CostInBillingCurrency,BillingCurrency
        08/12/2026,Microsoft.Compute,/subscriptions/sub-2/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1,9.5,USD
        """;

        var records = new CostExportCsvParser().Parse(new StringReader(csv), "sub-2").ToList();

        records.Should().ContainSingle();
        records[0].SubscriptionId.Should().Be("sub-2");
        records[0].ServiceName.Should().Be("Microsoft.Compute");
        records[0].ResourceId.Should().EndWith("/virtualMachines/vm1");
        records[0].Cost.Should().Be(9.5m);
    }

    [Fact]
    public void Parse_WhenRequiredColumnsAreMissing_ShouldThrow()
    {
        const string csv = "ServiceName,Currency\nAzure Storage,BRL\n";

        var act = () => new CostExportCsvParser().Parse(new StringReader(csv), "sub-1").ToList();

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*colunas obrigatórias*");
    }

    [Fact]
    public void Parse_WhenCsvSubscriptionDiffersFromPath_ShouldThrow()
    {
        const string csv = """
        Date,SubscriptionId,PreTaxCost
        2026-08-12,sub-other,10.00
        """;

        var act = () => new CostExportCsvParser().Parse(new StringReader(csv), "sub-path").ToList();

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*linha lógica 2*");
    }

    [Fact]
    public void Parse_ShouldReadRealWebDirectHeaderUsageDateTimeAndEmbeddedJson()
    {
        const string subscriptionId = "00000000-0000-0000-0000-00000000a006";
        const string csv = """
        ﻿SubscriptionGuid,ResourceGroup,ResourceLocation,UsageDateTime,MeterCategory,MeterSubCategory,MeterId,MeterName,MeterRegion,UsageQuantity,ResourceRate,PreTaxCost,ConsumedService,ResourceType,InstanceId,Tags,OfferId,AdditionalInfo,ServiceInfo1,ServiceInfo2,ServiceName,ServiceTier,Currency,UnitOfMeasure
        00000000-0000-0000-0000-00000000a006,rg,eastus,2026-08-19T00:00:00Z,Storage,Files,meter-1,Transactions,,1,0.1,1.234567890123456789E-3,Microsoft.Storage,microsoft.storage/storageaccounts,/subscriptions/00000000-0000-0000-0000-00000000a006/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/stg,"{""env"":""prod"",""description"":""value with , comma""}",offer,"{""note"":""escaped \""quote\"" and comma, ok""}",,,,Standard,USD,1
        """;

        var records = new CostExportCsvParser().Parse(
            new StringReader(csv),
            subscriptionId).ToList();

        records.Should().ContainSingle();
        records[0].UsageDate.Should().Be(new DateTime(2026, 8, 19));
        records[0].Cost.Should().Be(0.001234567890123456789m);
        records[0].ServiceName.Should().Be("Microsoft.Storage");
        records[0].ResourceId.Should().EndWith("/storageAccounts/stg");
        records[0].ResourceGroup.Should().Be("rg");
        records[0].ResourceLocation.Should().Be("eastus");
        records[0].ResourceType.Should().Be("microsoft.storage/storageaccounts");
        records[0].Tags.Should().Contain("description").And.Contain("value with , comma");
        records[0].Currency.Should().Be("USD");
    }

    [Fact]
    public void Parse_ShouldPreserveEveryUsageDateTimeFromCsv()
    {
        const string csv = """
        UsageDateTime,SubscriptionGuid,ServiceName,InstanceId,PreTaxCost,Currency
        2026-08-17T00:00:00Z,sub-1,Storage,/subscriptions/sub-1/resources/stg,1.25,USD
        2026-08-18T00:00:00Z,sub-1,Compute,/subscriptions/sub-1/resources/vm,2.75,USD
        """;

        var records = new CostExportCsvParser().Parse(new StringReader(csv), "sub-1").ToList();

        records.Select(record => record.UsageDate).Should().Equal(
            new DateTime(2026, 8, 17),
            new DateTime(2026, 8, 18));
    }

    [Fact]
    public void Parse_WhenCostIsInvalid_ShouldReportLogicalLineAndStop()
    {
        const string csv = """
        UsageDateTime,PreTaxCost
        2026-08-18,1.25
        2026-08-19,not-a-number
        """;

        var act = () => new CostExportCsvParser().Parse(
            new StringReader(csv),
            "sub-1").ToList();

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*Custo inválido*linha lógica 3*");
    }

    [Fact]
    public void Parse_WhenDateIsInvalid_ShouldReportLogicalLineAndStop()
    {
        const string csv = """
        UsageDateTime,PreTaxCost
        not-a-date,1.25
        """;

        var act = () => new CostExportCsvParser().Parse(
            new StringReader(csv),
            "sub-1").ToList();

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*Data inválida*linha lógica 2*");
    }
}
