using Azure;
using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class GovernanceBudgetServiceTests
{
    private const string Subscription = "11111111-1111-1111-1111-111111111111";
    private const string BudgetId = "22222222-2222-2222-2222-222222222222";
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
    private readonly Mock<IGovernanceBudgetRepository> _repository = new();
    private readonly Mock<ICostExportVersionStore> _versions = new();
    private readonly Mock<IGovernanceAuditLogRepository> _audit = new();
    private static GovernanceBudgetInput Input => new("Budget", Subscription, "2026-09", "BRL", 100m);

    [Fact]
    public async Task FullOfficialCoverage_UsesRealCostAndLinearEstimate()
    {
        SetupBudget(); SetupDays(Enumerable.Range(1, 8).Select(day => Snapshot(day, 10m)));
        var result = (await Create().EvaluateAsync(BudgetId, Now))!;
        result.ObservedCost.Should().Be(80m);
        result.ConsumptionPercent.Should().Be(80m);
        result.EstimatedMonthEnd.Should().Be(300m);
        result.ForecastReliable.Should().BeTrue();
        result.ForecastExceedsBudget.Should().BeTrue();
        result.ReachedThresholds.Should().Equal(50, 80);
        result.NotificationDelivery.Should().Be("notConfigured");
        result.ExpectedDays.Should().Be(8);
        result.ObservedDays.Should().Be(8);
    }

    [Fact]
    public async Task Gap_DoesNotBecomeZero_AndForecastIsUnreliable()
    {
        SetupBudget(); SetupDays([Snapshot(1, 50m), Snapshot(8, 60m)]);
        var result = (await Create().EvaluateAsync(BudgetId, Now))!;
        result.ObservedCost.Should().Be(110m);
        result.ReachedThresholds.Should().Equal(50, 80, 100);
        result.MissingDates.Should().HaveCount(6);
        result.EstimatedMonthEnd.Should().Be(1650m);
        result.ForecastReliable.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoReadableIndex_ReturnsNullAmounts(bool failedRead)
    {
        SetupBudget();
        if (failedRead) _versions.Setup(v => v.LoadIndexAsync(Subscription, "Cutover", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(503, "Unavailable"));
        var result = (await Create().EvaluateAsync(BudgetId, Now))!;
        result.ObservedCost.Should().BeNull();
        result.EstimatedMonthEnd.Should().BeNull();
        result.ForecastExceedsBudget.Should().BeNull();
        result.ConsumptionPercent.Should().BeNull();
        result.ForecastReliable.Should().BeFalse();
        result.ReachedThresholds.Should().BeEmpty();
    }

    [Fact]
    public async Task CurrencyMismatch_IsUnavailable_NotZeroOrConverted()
    {
        SetupBudget(); SetupDays([Snapshot(1, 100m, currency: "USD")]);
        var result = (await Create().EvaluateAsync(BudgetId, Now))!;
        result.ObservedCost.Should().BeNull();
        result.ObservedDays.Should().Be(0);
    }

    [Fact]
    public async Task MixedCurrencies_AreNeverSummedTogether()
    {
        SetupBudget();
        var day = Snapshot(1, 10m);
        day.ByService[0].Currency = "BRL";
        SetupDays([new CostExportDailySnapshot { SubscriptionId = Subscription, PublicationMode = "Cutover", Date = day.Date,
            ByService = [day.ByService[0], new() { Currency = "USD", TotalCost = 999m }] }]);
        var result = (await Create().EvaluateAsync(BudgetId, Now))!;
        result.ObservedCost.Should().Be(10m);
    }

    [Theory]
    [InlineData("BRL", true)]
    [InlineData("USD", false)]
    [InlineData(null, false)]
    public async Task Tombstone_OnlyConfirmsZeroForItsExplicitCurrency(string? currency, bool knownZero)
    {
        SetupBudget(); SetupDays([new CostExportDailySnapshot { SubscriptionId = Subscription, PublicationMode = "Cutover",
            Date = new(2026, 9, 1), IsTombstone = true, TombstoneCurrency = currency }]);
        var result = (await Create().EvaluateAsync(BudgetId, Now))!;
        result.ObservedCost.Should().Be(knownZero ? 0m : null);
        result.ObservedDays.Should().Be(knownZero ? 1 : 0);
    }

    [Fact]
    public async Task ExplicitZeroRows_KeepZeroAndDoNotTriggerThresholds()
    {
        SetupBudget(); SetupDays([Snapshot(1, 0m)]);
        var result = (await Create().EvaluateAsync(BudgetId, Now))!;
        result.ObservedCost.Should().Be(0m);
        result.EstimatedMonthEnd.Should().Be(0m);
        result.ReachedThresholds.Should().BeEmpty();
    }

    [Fact]
    public async Task Credit_IsNotClampedIntoPositiveSpending()
    {
        SetupBudget(); SetupDays([Snapshot(1, -10m)]);
        var result = (await Create().EvaluateAsync(BudgetId, Now))!;
        result.ObservedCost.Should().Be(-10m);
        result.ReachedThresholds.Should().BeEmpty();
    }

    [Fact]
    public async Task ShadowNeverProducesReliableFinancialForecast()
    {
        SetupBudget(); SetupDays(Enumerable.Range(1, 8).Select(day => Snapshot(day, 10m, mode: "Shadow")), "Shadow");
        var result = (await Create(cutover: false).EvaluateAsync(BudgetId, Now))!;
        result.ObservedCost.Should().Be(80m);
        result.ForecastReliable.Should().BeFalse();
        result.PublicationMode.Should().Be("Shadow");
    }

    [Fact]
    public async Task CostCenter_UsesImportedAttributionAndFlagsUnassignedCost()
    {
        SetupBudget(Input with { CostCenter = "NAP" });
        SetupDays(Enumerable.Range(1, 8).Select(day => Snapshot(day, 100m, resources:
            [new() { CostCenter = "nap", Currency = "BRL", TotalCost = 40m }, new() { Currency = "BRL", TotalCost = 60m }])));
        var result = (await Create().EvaluateAsync(BudgetId, Now))!;
        result.ObservedCost.Should().Be(320m);
        result.ObservedDays.Should().Be(8);
        result.ForecastReliable.Should().BeFalse();
        result.Warnings.Should().Contain(w => w.Contains("Atribuição incompleta"));
    }

    [Fact]
    public async Task CostCenter_RawTagsReuseExistingParser()
    {
        SetupBudget(Input with { CostCenter = "NAP" });
        SetupDays(Enumerable.Range(1, 8).Select(day => Snapshot(day, 10m,
            resources: [new() { Tags = "{\"CentrodeCusto\":\"NAP\"}", Currency = "BRL", TotalCost = 10m }])));
        var result = (await Create().EvaluateAsync(BudgetId, Now))!;
        result.ObservedCost.Should().Be(80m);
        result.ForecastReliable.Should().BeTrue();
    }

    [Theory]
    [InlineData("Other", true)]
    [InlineData("", false)]
    public async Task AbsentCenter_IsZeroOnlyWhenAllCostsAreAttributed(string otherCenter, bool complete)
    {
        SetupBudget(Input with { CostCenter = "NAP" });
        SetupDays([Snapshot(1, 10m, resources: [new() { CostCenter = otherCenter, Currency = "BRL", TotalCost = 10m }])]);
        var result = (await Create().EvaluateAsync(BudgetId, Now))!;
        result.ObservedCost.Should().Be(complete ? 0m : null);
    }

    [Fact]
    public async Task BrokenSnapshot_IsGapNotServerSuccessWithZero()
    {
        SetupBudget(); var day = Snapshot(1, 100m); SetupDays([day]);
        _versions.Setup(v => v.LoadSnapshotAsync(Subscription, "Cutover", day.Revision, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "Missing snapshot"));
        var result = (await Create().EvaluateAsync(BudgetId, Now))!;
        result.ObservedCost.Should().BeNull();
        result.MissingDates.Should().Contain("2026-09-01");
    }

    [Fact]
    public async Task WrongRevisionContent_IsRejected()
    {
        SetupBudget(); var day = Snapshot(1, 100m); SetupDays([day]);
        _versions.Setup(v => v.LoadSnapshotAsync(Subscription, "Cutover", day.Revision, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot(1, 999m));
        var result = (await Create().EvaluateAsync(BudgetId, Now))!;
        result.ObservedCost.Should().BeNull();
    }

    [Fact]
    public async Task FutureMonth_HasNoExpectedDays_AndDoesNotReadCosts()
    {
        SetupBudget(Input with { Month = "2026-10" });
        var result = (await Create().EvaluateAsync(BudgetId, Now))!;
        result.ExpectedDays.Should().Be(0);
        result.ObservedCost.Should().BeNull();
        _versions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ClosedMonth_UsesEntireCalendarMonthIncludingLeapDay()
    {
        SetupBudget(Input with { Month = "2024-02" });
        var result = (await Create().EvaluateAsync(BudgetId, Now))!;
        result.ExpectedDays.Should().Be(29);
        result.MissingDates.Should().Contain("2024-02-29");
    }

    [Fact]
    public async Task Save_NormalizesAndPreservesConcurrencyToken()
    {
        SetupBudget();
        _repository.Setup(r => r.SaveAsync(It.IsAny<GovernanceBudget>(), "\"version\"", It.IsAny<CancellationToken>()))
            .ReturnsAsync((GovernanceBudget budget, string? _, CancellationToken _) => budget);
        _repository.Setup(r => r.GetAsync(BudgetId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GovernanceBudget(BudgetId, Input, Now, "previous-actor", "\"version\""));
        var saved = await Create().SaveAsync(BudgetId, Input with { Name = " Budget ", Currency = "brl", Version = "\"version\"" }, "admin", Now);
        saved.Definition.Name.Should().Be("Budget");
        saved.Definition.Currency.Should().Be("BRL");
        saved.Definition.Version.Should().BeNull();
        saved.UpdatedBy.Should().Be("admin");
        _repository.Verify(r => r.SaveAsync(It.IsAny<GovernanceBudget>(), "\"version\"", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("precision")]
    [InlineData("name")]
    [InlineData("subscription")]
    [InlineData("month")]
    [InlineData("currency")]
    [InlineData("center")]
    [InlineData("version")]
    public void InvalidInput_IsRejected(string field)
    {
        var input = field switch
        {
            "amount" => Input with { Amount = 0 }, "precision" => Input with { Amount = 1.001m },
            "name" => Input with { Name = "" }, "subscription" => Input with { SubscriptionId = "../x" },
            "month" => Input with { Month = "2026-13" }, "currency" => Input with { Currency = "R$" },
            "center" => Input with { CostCenter = " " }, _ => Input with { Version = "*" }
        };
        FluentActions.Invoking(() => GovernanceBudgetService.Validate(input)).Should().Throw<ArgumentException>();
    }

    private void SetupBudget(GovernanceBudgetInput? definition = null) => _repository
        .Setup(r => r.GetAsync(BudgetId, It.IsAny<CancellationToken>()))
        .ReturnsAsync(new GovernanceBudget(BudgetId, definition ?? Input, Now, "admin", "\"v1\""));

    private void SetupDays(IEnumerable<CostExportDailySnapshot> values, string mode = "Cutover")
    {
        var snapshots = values.ToArray();
        var index = new CostExportRevisionIndex { SubscriptionId = Subscription, PublicationMode = mode }.Merge(snapshots);
        _versions.Setup(v => v.LoadIndexAsync(Subscription, mode, It.IsAny<CancellationToken>())).ReturnsAsync(index);
        foreach (var snapshot in snapshots)
            _versions.Setup(v => v.LoadSnapshotAsync(Subscription, mode, snapshot.Revision, It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);
    }

    private static CostExportDailySnapshot Snapshot(int day, decimal cost, string currency = "BRL", string mode = "Cutover",
        IReadOnlyList<CostByResourceRow>? resources = null) => new()
    {
        SubscriptionId = Subscription, PublicationMode = mode, Date = new(2026, 9, day),
        ByService = [new() { Currency = currency, TotalCost = cost }], ByResource = resources ?? []
    };

    private GovernanceBudgetService Create(bool cutover = true)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EnableCostExportImport"] = "true", ["COST_EXPORT_SUBSCRIPTIONS"] = Subscription,
            ["COST_EXPORT_CUTOVER_SUBSCRIPTIONS"] = cutover ? Subscription : ""
        }).Build();
        var health = new GovernanceDataHealthService(_versions.Object, Mock.Of<ICostStorageRepository>(), config,
            NullLogger<GovernanceDataHealthService>.Instance);
        return new(_repository.Object, _versions.Object, health, config, NullLogger<GovernanceBudgetService>.Instance, _audit.Object);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_AuditsIntentThenMutationThenResult(bool update)
    {
        SetupBudget();
        var sequence = new List<string>();
        var events = new List<AuditLogDocument>();
        _audit.Setup(a => a.AppendAsync(It.IsAny<AuditLogDocument>(), It.IsAny<CancellationToken>()))
            .Callback<AuditLogDocument, CancellationToken>((e, _) => { events.Add(e); sequence.Add((string)e.Metadata!["outcome"]); })
            .Returns(Task.CompletedTask);
        _repository.Setup(r => r.SaveAsync(It.IsAny<GovernanceBudget>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => sequence.Add("Write"))
            .ReturnsAsync((GovernanceBudget b, string? _, CancellationToken _) => b with { Version = "\"v2\"" });
        await Create().SaveAsync(BudgetId, Input with { Version = update ? "\"v1\"" : null }, "token-oid", Now);
        sequence.Should().Equal("Intent", "Write", "Succeeded");
        events.Should().OnlyContain(e => e.PerformedBy == "token-oid" && e.EntityId == BudgetId && e.Action == (update ? "Update" : "Create"));
        events[0].PerformedAt.Should().Be(Now.UtcDateTime);
        events[0].Metadata!["parameters"].Should().BeOfType<GovernanceBudgetInput>();
        events[1].Metadata!["operationId"].Should().Be(events[0].Metadata!["operationId"]);
        events[1].After.Should().BeOfType<GovernanceBudget>().Which.Version.Should().Be("\"v2\"");
        if (update) events[1].Before.Should().BeOfType<GovernanceBudget>();
    }

    [Fact]
    public async Task Delete_LeavesDurableAuditIncludingActorVersionAndDeletedDefinition()
    {
        SetupBudget();
        await Create().DeleteAsync(BudgetId, "\"v1\"", "token-oid", Now);
        _audit.Verify(a => a.AppendAsync(It.Is<AuditLogDocument>(e => e.Action == "Delete" && e.PerformedBy == "token-oid"
            && (string)e.Metadata!["outcome"] == "Succeeded" && (string)e.Metadata["expectedVersion"] == "\"v1\""
            && e.Before is GovernanceBudget && e.After == null), It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(r => r.DeleteAsync(BudgetId, "\"v1\"", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IntentFailure_FailsClosedWithoutAnyBudgetIO(bool delete)
    {
        _audit.Setup(a => a.AppendAsync(It.IsAny<AuditLogDocument>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("offline"));
        Func<Task> act = () => delete ? Create().DeleteAsync(BudgetId, "\"v1\"", "actor", Now)
            : Create().SaveAsync(BudgetId, Input, "actor", Now);
        (await act.Should().ThrowAsync<GovernanceAuditIncompleteException>()).Which.Applied.Should().BeFalse();
        _repository.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalAuditFailure_DoesNotRetryOrClaimMutationFailed(bool delete)
    {
        SetupBudget();
        _repository.Setup(r => r.SaveAsync(It.IsAny<GovernanceBudget>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((GovernanceBudget b, string? _, CancellationToken _) => b);
        _audit.SetupSequence(a => a.AppendAsync(It.IsAny<AuditLogDocument>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask).ThrowsAsync(new IOException("offline"));
        Func<Task> act = () => delete ? Create().DeleteAsync(BudgetId, "\"v1\"", "actor", Now)
            : Create().SaveAsync(BudgetId, Input, "actor", Now);
        var error = (await act.Should().ThrowAsync<GovernanceAuditIncompleteException>()).Which;
        error.Applied.Should().BeTrue();
        Guid.TryParse(error.OperationId, out _).Should().BeTrue();
        if (delete) _repository.Verify(r => r.DeleteAsync(BudgetId, "\"v1\"", It.IsAny<CancellationToken>()), Times.Once);
        else _repository.Verify(r => r.SaveAsync(It.IsAny<GovernanceBudget>(), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConcurrentDelete_ConflictIsAuditedWithoutDeletingNewerVersion()
    {
        SetupBudget();
        _repository.Setup(r => r.DeleteAsync(BudgetId, "\"v1\"", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(412, "Changed after read"));
        await FluentActions.Invoking(() => Create().DeleteAsync(BudgetId, "\"v1\"", "actor", Now)).Should().ThrowAsync<RequestFailedException>();
        _audit.Verify(a => a.AppendAsync(It.Is<AuditLogDocument>(e => (string)e.Metadata!["outcome"] == "Rejected"
            && (int)e.Metadata["storageStatus"] == 412), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Timeout_IsIndeterminateAndAuditIgnoresCancelledRequestToken()
    {
        using var cancellation = new CancellationTokenSource();
        _repository.Setup(r => r.SaveAsync(It.IsAny<GovernanceBudget>(), null, It.IsAny<CancellationToken>()))
            .Callback(() => cancellation.Cancel()).ThrowsAsync(new OperationCanceledException());
        var error = await FluentActions.Invoking(() => Create().SaveAsync(BudgetId, Input, "actor", Now, cancellation.Token))
            .Should().ThrowAsync<GovernanceAuditIncompleteException>();
        error.Which.Applied.Should().BeNull();
        _audit.Verify(a => a.AppendAsync(It.Is<AuditLogDocument>(e => (string)e.Metadata!["outcome"] == "Indeterminate"),
            It.Is<CancellationToken>(ct => ct != cancellation.Token && !ct.IsCancellationRequested)), Times.Once);
    }

    [Fact]
    public async Task MissingStableActor_FailsBeforeAuditOrStorage()
    {
        await FluentActions.Invoking(() => Create().SaveAsync(BudgetId, Input, "", Now)).Should().ThrowAsync<ArgumentException>();
        _audit.VerifyNoOtherCalls(); _repository.VerifyNoOtherCalls();
    }
}