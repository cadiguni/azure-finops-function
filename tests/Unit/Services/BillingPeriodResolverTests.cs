using System.Net;
using Azure.Core;
using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class BillingPeriodResolverTests
{
    [Fact]
    public async Task ResolveAsync_WhenManualStartDayIsOne_ShouldUseCalendarMonthWithMediumConfidence()
    {
        var sut = BuildResolver(new Dictionary<string, string?>
        {
            ["CostAnomalyUseAzureBillingPeriod"] = "false",
            ["CostAnomalySubscriptionTurnDayOverrides"] = "sub-1=1"
        });

        var result = await sut.ResolveAsync("sub-1", new DateTime(2026, 6, 25));

        result.CurrentPeriodFrom.Should().Be("2026-06-01");
        result.CurrentPeriodTo.Should().Be("2026-06-30");
        result.BillingCycleStartDay.Should().Be(1);
        result.Source.Should().Be(BillingPeriodSources.ManualConfiguration);
        result.Confidence.Should().Be(BillingPeriodConfidence.Medium);
    }

    [Fact]
    public async Task ResolveAsync_WhenManualStartDayIsFifteenAndDateBeforeTurn_ShouldUsePreviousMonth()
    {
        var sut = BuildResolver(new Dictionary<string, string?>
        {
            ["CostAnomalyUseAzureBillingPeriod"] = "false",
            ["CostAnomalySubscriptionTurnDayOverrides"] = "sub-1=15"
        });

        var result = await sut.ResolveAsync("sub-1", new DateTime(2026, 6, 10));

        result.CurrentPeriodFrom.Should().Be("2026-05-15");
        result.CurrentPeriodTo.Should().Be("2026-06-14");
    }

    [Fact]
    public async Task ResolveAsync_WhenManualStartDayIsFifteenAndDateAfterTurn_ShouldUseCurrentMonth()
    {
        var sut = BuildResolver(new Dictionary<string, string?>
        {
            ["CostAnomalyUseAzureBillingPeriod"] = "false",
            ["CostAnomalySubscriptionTurnDayOverrides"] = "sub-1=15"
        });

        var result = await sut.ResolveAsync("sub-1", new DateTime(2026, 6, 25));

        result.CurrentPeriodFrom.Should().Be("2026-06-15");
        result.CurrentPeriodTo.Should().Be("2026-07-14");
    }

    [Fact]
    public async Task ResolveAsync_WhenManualStartDayCrossesYear_ShouldUsePreviousYear()
    {
        var sut = BuildResolver(new Dictionary<string, string?>
        {
            ["CostAnomalyUseAzureBillingPeriod"] = "false",
            ["CostAnomalySubscriptionTurnDayOverrides"] = "sub-1=15"
        });

        var result = await sut.ResolveAsync("sub-1", new DateTime(2026, 1, 10));

        result.CurrentPeriodFrom.Should().Be("2025-12-15");
        result.CurrentPeriodTo.Should().Be("2026-01-14");
    }

    [Fact]
    public async Task ResolveAsync_WhenManualStartDayUsesFebruary_ShouldCalculatePeriodEnd()
    {
        var sut = BuildResolver(new Dictionary<string, string?>
        {
            ["CostAnomalyUseAzureBillingPeriod"] = "false",
            ["CostAnomalySubscriptionTurnDayOverrides"] = "sub-1=15"
        });

        var result = await sut.ResolveAsync("sub-1", new DateTime(2026, 2, 20));

        result.CurrentPeriodFrom.Should().Be("2026-02-15");
        result.CurrentPeriodTo.Should().Be("2026-03-14");
    }

    [Fact]
    public async Task ResolveAsync_WhenAzureReturns404AndNoManualConfig_ShouldFallbackToCalendarMonth()
    {
        var sut = BuildResolver(
            new Dictionary<string, string?>
            {
                ["CostAnomalyUseAzureBillingPeriod"] = "true"
            },
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"error":{"code":"InvalidResourceType"}}""")
            });

        var result = await sut.ResolveAsync("sub-1", new DateTime(2026, 6, 25));

        result.CurrentPeriodFrom.Should().Be("2026-06-01");
        result.CurrentPeriodTo.Should().Be("2026-06-30");
        result.Source.Should().Be(BillingPeriodSources.DefaultCalendarMonth);
        result.Confidence.Should().Be(BillingPeriodConfidence.Low);
    }

    [Fact]
    public async Task ResolveAsync_WhenAzurePeriodCrossesMonths_ShouldUseInclusiveEndDate()
    {
        var sut = BuildResolver(
            new Dictionary<string, string?>
            {
                ["CostAnomalyUseAzureBillingPeriod"] = "true"
            },
            SuccessfulPeriodResponse("2026-06-15", "2026-07-14"));

        var result = await sut.ResolveAsync("sub-1", new DateTime(2026, 7, 14));

        result.CurrentPeriodFrom.Should().Be("2026-06-15");
        result.CurrentPeriodTo.Should().Be("2026-07-14");
        result.Source.Should().Be(BillingPeriodSources.AzureBillingPeriodsApi);
        result.Confidence.Should().Be(BillingPeriodConfidence.High);
    }

    [Fact]
    public async Task ResolveAsync_WhenAzurePeriodCrossesYear_ShouldSetTurnToDayAfterEnd()
    {
        var sut = BuildResolver(
            new Dictionary<string, string?>
            {
                ["CostAnomalyUseAzureBillingPeriod"] = "true"
            },
            SuccessfulPeriodResponse("2025-12-15", "2026-01-14"));

        var result = await sut.ResolveAsync("sub-1", new DateTime(2026, 1, 10));
        var windows = await sut.ResolveCycleWindowsAsync(
            ["sub-1"],
            new DateTime(2026, 1, 10));

        result.CurrentPeriodFrom.Should().Be("2025-12-15");
        result.CurrentPeriodTo.Should().Be("2026-01-14");
        windows["sub-1"].CycleTurnDate.Should().Be(new DateTime(2026, 1, 15));
    }

    [Fact]
    public async Task ResolveAsync_WhenNoConfiguration_ShouldFallbackToCalendarMonth()
    {
        var sut = BuildResolver(new Dictionary<string, string?>
        {
            ["CostAnomalyUseAzureBillingPeriod"] = "false"
        });

        var result = await sut.ResolveAsync("sub-1", new DateTime(2026, 6, 25));

        result.CurrentPeriodFrom.Should().Be("2026-06-01");
        result.CurrentPeriodTo.Should().Be("2026-06-30");
        result.Source.Should().Be(BillingPeriodSources.DefaultCalendarMonth);
        result.Confidence.Should().Be(BillingPeriodConfidence.Low);
    }

    [Fact]
    public async Task ResolveAsync_WhenApiVersionIsNotConfigured_ShouldUseSupportedPreviewVersion()
    {
        var (sut, factory) = BuildResolverWithFactory(
            new Dictionary<string, string?>
            {
                ["CostAnomalyUseAzureBillingPeriod"] = "true"
            },
            new HttpResponseMessage(HttpStatusCode.NotFound));

        await sut.ResolveAsync("sub-1", new DateTime(2026, 6, 25));

        factory.LastRequestUri.Should().NotBeNull();
        factory.LastRequestUri!.Query.Should().Contain("api-version=2018-03-01-preview");
    }

    private static SubscriptionBillingCycleService BuildResolver(
        Dictionary<string, string?> settings,
        HttpResponseMessage? response = null)
    {
        return BuildResolverWithFactory(settings, response).Resolver;
    }

    private static (
        SubscriptionBillingCycleService Resolver,
        StubHttpClientFactory Factory) BuildResolverWithFactory(
        Dictionary<string, string?> settings,
        HttpResponseMessage? response = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var factory = new StubHttpClientFactory(response ?? new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"value":[]}""")
        });

        var resolver = new SubscriptionBillingCycleService(
            new StubTokenCredential(),
            factory,
            new HttpRetryService(new NullLogger<HttpRetryService>()),
            new StubCostManagementClient(),
            configuration,
            new NullLogger<SubscriptionBillingCycleService>());
        return (resolver, factory);
    }

    private static HttpResponseMessage SuccessfulPeriodResponse(string startDate, string endDate)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""
                {
                  "value": [
                    {
                      "properties": {
                        "billingPeriodStartDate": "{{startDate}}",
                        "billingPeriodEndDate": "{{endDate}}"
                      }
                    }
                  ]
                }
                """)
        };
    }

    private sealed class StubTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new("token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(new AccessToken("token", DateTimeOffset.UtcNow.AddHours(1)));
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpResponseMessage _response;

        public StubHttpClientFactory(HttpResponseMessage response)
        {
            _response = response;
        }

        public Uri? LastRequestUri { get; private set; }

        public HttpClient CreateClient(string name)
            => new(new StubHandler(_response, uri => LastRequestUri = uri));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        private readonly Action<Uri?> _captureRequestUri;

        public StubHandler(HttpResponseMessage response, Action<Uri?> captureRequestUri)
        {
            _response = response;
            _captureRequestUri = captureRequestUri;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _captureRequestUri(request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(_response.StatusCode)
            {
                Content = _response.Content == null
                    ? null
                    : new StringContent(_response.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult())
            });
        }
    }

    private sealed class StubCostManagementClient : ICostManagementClient
    {
        public Task<CostByServiceQueryResponse> QueryCostByServiceAsync(string subscriptionId, DateTime dateStartUtc, DateTime dateEndUtc, string granularity, string? serviceFilter = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new CostByServiceQueryResponse());

        public Task<CostByServiceQueryResponse> QueryCostByServiceForBillingMonthToDateAsync(string subscriptionId, string granularity, CancellationToken cancellationToken = default)
            => Task.FromResult(new CostByServiceQueryResponse());

        public Task<CostByResourceQueryResponse> QueryCostByResourceAsync(string subscriptionId, DateTime dateStartUtc, DateTime dateEndUtc, string granularity, string? serviceFilter = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new CostByResourceQueryResponse());

        public Task<IReadOnlyCollection<CostDimensionInfo>> GetAvailableDimensionsAsync(string subscriptionId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<CostDimensionInfo>>(Array.Empty<CostDimensionInfo>());

        public Task<IReadOnlyCollection<CostDiagnosticRow>> QueryCostDiagnosticsAsync(string subscriptionId, DateTime dateStartUtc, DateTime dateEndUtc, string granularity, IReadOnlyCollection<string> groupingDimensions, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<CostDiagnosticRow>>(Array.Empty<CostDiagnosticRow>());
    }
}
