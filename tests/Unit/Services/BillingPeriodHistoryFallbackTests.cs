using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

/// <summary>
/// O ciclo oficial vira todo mês, então a janela persistida para de cobrir a data consultada.
/// Antes disso derrubava a assinatura para o mês calendário; o dia de virada precisa sobreviver.
/// </summary>
public class BillingPeriodHistoryFallbackTests
{
    [Fact]
    public void DeriveWindowFromHistory_ShouldProjectCurrentCycleAfterPersistedWindowExpired()
    {
        // Ciclo oficial conhecido: 07/08 a 06/09. A consulta é de 10/09, já fora dessa janela.
        var window = SubscriptionBillingCycleService.DeriveWindowFromHistory(
            [Window("sub-1", "2026-08-07", "2026-09-06")],
            "sub-1",
            new DateTime(2026, 9, 10));

        window.Should().NotBeNull();
        window!.CycleStartDate.Should().Be(new DateTime(2026, 9, 7));
        window.CycleTurnDate.Should().Be(new DateTime(2026, 10, 7));
        window.Confidence.Should().Be(BillingPeriodConfidence.Medium);
        window.Source.Should().Be(BillingPeriodSources.AzureBillingPeriodsApi);
    }

    [Fact]
    public void DeriveWindowFromHistory_ShouldUsePreviousCycleWhenReferenceIsBeforeTurnDay()
    {
        var window = SubscriptionBillingCycleService.DeriveWindowFromHistory(
            [Window("sub-1", "2026-08-07", "2026-09-06")],
            "sub-1",
            new DateTime(2026, 9, 3));

        window!.CycleStartDate.Should().Be(new DateTime(2026, 8, 7));
        window.CycleTurnDate.Should().Be(new DateTime(2026, 9, 7));
    }

    [Fact]
    public void DeriveWindowFromHistory_ShouldPreferTheMostRecentlyRetrievedWindow()
    {
        var window = SubscriptionBillingCycleService.DeriveWindowFromHistory(
            [
                Window("sub-1", "2026-05-15", "2026-06-14", retrievedAt: new DateTime(2026, 6, 1)),
                Window("sub-1", "2026-08-07", "2026-09-06", retrievedAt: new DateTime(2026, 9, 1))
            ],
            "sub-1",
            new DateTime(2026, 9, 10));

        window!.CycleStartDate.Day.Should().Be(7);
    }

    [Fact]
    public void DeriveWindowFromHistory_ShouldClampTurnDayOnShorterMonths()
    {
        // Virada no dia 31 não existe em fevereiro: precisa cair no último dia do mês.
        var window = SubscriptionBillingCycleService.DeriveWindowFromHistory(
            [Window("sub-1", "2026-01-31", "2026-02-27")],
            "sub-1",
            new DateTime(2026, 2, 28));

        window!.CycleStartDate.Should().Be(new DateTime(2026, 2, 28));
    }

    [Fact]
    public void DeriveWindowFromHistory_ShouldIgnoreOtherSubscriptions()
    {
        SubscriptionBillingCycleService.DeriveWindowFromHistory(
            [Window("sub-outra", "2026-08-07", "2026-09-06")],
            "sub-1",
            new DateTime(2026, 9, 10))
            .Should().BeNull();
    }

    [Fact]
    public void DeriveWindowFromHistory_ShouldReturnNullWithoutHistory()
    {
        SubscriptionBillingCycleService.DeriveWindowFromHistory([], "sub-1", new DateTime(2026, 9, 10))
            .Should().BeNull();
    }

    private static BillingPeriodOfficialWindowInfo Window(
        string subscriptionId,
        string from,
        string to,
        DateTime? retrievedAt = null) => new()
    {
        SubscriptionId = subscriptionId,
        CurrentPeriodFrom = from,
        CurrentPeriodTo = to,
        RetrievedAt = retrievedAt ?? new DateTime(2026, 9, 1),
        RetrievedBySource = BillingPeriodSources.AzureBillingPeriodsApi
    };
}
