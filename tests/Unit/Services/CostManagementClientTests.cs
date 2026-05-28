using Azure.Core;
using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class CostManagementClientTests
{
    [Fact]
    public async Task QueryCostByServiceAsync_WhenSubscriptionIsEmpty_ShouldThrowArgumentException()
    {
        var handler = new SequenceHttpMessageHandler(new[] { CreateResponse(HttpStatusCode.OK, "{}") });
        var sut = BuildClient(handler);

        var act = () => sut.QueryCostByServiceAsync("", DateTime.UtcNow, DateTime.UtcNow, "None");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*SubscriptionId é obrigatório*");
    }

    [Fact]
    public async Task QueryCostByServiceAsync_ShouldParseServiceRowsAndCurrency()
    {
        var json = """
        {
          "properties": {
            "columns": [
              { "name": "ServiceName", "type": "String" },
              { "name": "PreTaxCost", "type": "Number" },
              { "name": "Currency", "type": "String" }
            ],
            "rows": [
              [ "Azure App Service", 123.45, "BRL" ],
              [ "Azure Storage", 67.89, "BRL" ]
            ]
          }
        }
        """;

        var handler = new SequenceHttpMessageHandler(new[]
        {
            CreateResponse(HttpStatusCode.OK, json)
        });

        var sut = BuildClient(handler);

        var result = await sut.QueryCostByServiceAsync(
            "sub-1",
            new DateTime(2026, 2, 20),
            new DateTime(2026, 2, 20),
            "None");

        result.SubscriptionId.Should().Be("sub-1");
        result.Currency.Should().Be("BRL");
        result.Rows.Should().HaveCount(2);
        result.Rows[0].Label.Should().Be("Azure App Service");
        result.Rows[0].TotalCost.Should().Be(123.45m);
        result.Rows[1].Label.Should().Be("Azure Storage");
    }

    [Fact]
    public async Task QueryCostByServiceAsync_WhenReceives429_ShouldRetryAndSucceed()
    {
        var first = CreateResponse(HttpStatusCode.TooManyRequests, "rate limited");
        first.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));

        var second = CreateResponse(HttpStatusCode.OK, """
        {
          "properties": {
            "columns": [
              { "name": "ServiceName", "type": "String" },
              { "name": "PreTaxCost", "type": "Number" },
              { "name": "Currency", "type": "String" }
            ],
            "rows": [
              [ "Azure SQL", 10.00, "BRL" ]
            ]
          }
        }
        """);

        var handler = new SequenceHttpMessageHandler(new[] { first, second });
        var sut = BuildClient(handler);

        var result = await sut.QueryCostByServiceAsync(
            "sub-2",
            new DateTime(2026, 2, 20),
            new DateTime(2026, 2, 20),
            "None");

        handler.SendCount.Should().Be(2);
        result.Rows.Should().ContainSingle();
        result.Rows[0].Label.Should().Be("Azure SQL");
        result.Rows[0].TotalCost.Should().Be(10.00m);
    }

    [Fact]
    public async Task QueryCostByServiceAsync_ShouldSupportFallbackColumnNamesAndParseUsageDate()
    {
        var json = """
        {
          "properties": {
            "columns": [
              { "name": "Service", "type": "String" },
              { "name": "Cost", "type": "Number" },
              { "name": "CurrencyCode", "type": "String" },
              { "name": "UsageDate", "type": "Number" }
            ],
            "rows": [
              [ "Azure Front Door", 80.25, "USD", 20260220 ]
            ]
          }
        }
        """;

        var handler = new SequenceHttpMessageHandler(new[]
        {
            CreateResponse(HttpStatusCode.OK, json)
        });

        var sut = BuildClient(handler);

        var result = await sut.QueryCostByServiceAsync(
            "sub-fallback",
            new DateTime(2026, 2, 20),
            new DateTime(2026, 2, 20),
            "Daily");

        result.Rows.Should().ContainSingle();
        result.Rows[0].Label.Should().Be("Azure Front Door");
        result.Rows[0].Currency.Should().Be("USD");
        result.Rows[0].TotalCost.Should().Be(80.25m);
        result.Rows[0].UsageDate.Should().NotBeNull();
    }

    [Fact]
    public async Task QueryCostByServiceAsync_WhenResponseHasMissingColumns_ShouldUseDefaults()
    {
        var json = """
        {
          "properties": {
            "columns": [
              { "name": "ServiceName", "type": "String" }
            ],
            "rows": [
              [ "Azure Storage" ]
            ]
          }
        }
        """;

        var handler = new SequenceHttpMessageHandler(new[]
        {
            CreateResponse(HttpStatusCode.OK, json)
        });

        var sut = BuildClient(handler);

        var result = await sut.QueryCostByServiceAsync(
            "sub-defaults",
            new DateTime(2026, 2, 20),
            new DateTime(2026, 2, 20),
            "None");

        result.Rows.Should().ContainSingle();
        result.Rows[0].Label.Should().Be("Azure Storage");
        result.Rows[0].Currency.Should().Be("BRL");
        result.Rows[0].TotalCost.Should().Be(0m);
    }

    [Fact]
    public async Task QueryCostByServiceAsync_WhenErrorIsNotTransient_ShouldThrowWithoutRetry()
    {
        var handler = new SequenceHttpMessageHandler(new[]
        {
            CreateResponse(HttpStatusCode.BadRequest, "{\"error\":\"invalid query\"}")
        });
        var sut = BuildClient(handler);

        var act = () => sut.QueryCostByServiceAsync(
            "sub-bad-request",
            new DateTime(2026, 2, 20),
            new DateTime(2026, 2, 20),
            "None");

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*400*sub-bad-request*");
        handler.SendCount.Should().Be(1);
    }

    [Fact]
    public async Task QueryCostByResourceAsync_ShouldParseResourceIdAndName()
    {
        var json = """
        {
          "properties": {
            "columns": [
              { "name": "ResourceId", "type": "String" },
              { "name": "ServiceName", "type": "String" },
              { "name": "PreTaxCost", "type": "Number" },
              { "name": "Currency", "type": "String" }
            ],
            "rows": [
              [ "/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.Web/sites/web-app-a", "Azure App Service", 321.45, "BRL" ]
            ]
          }
        }
        """;

        var handler = new SequenceHttpMessageHandler(new[]
        {
            CreateResponse(HttpStatusCode.OK, json)
        });

        var sut = BuildClient(handler);

        var result = await sut.QueryCostByResourceAsync(
            "sub-1",
            new DateTime(2026, 2, 20),
            new DateTime(2026, 2, 20),
            "None",
            "Azure App Service");

        result.Rows.Should().ContainSingle();
        result.Rows[0].ResourceId.Should().Contain("/sites/web-app-a");
        result.Rows[0].Label.Should().Be("web-app-a");
        result.Rows[0].ServiceName.Should().Be("Azure App Service");
        result.Rows[0].TotalCost.Should().Be(321.45m);
    }

    private static CostManagementClient BuildClient(HttpMessageHandler handler)
    {
        var httpClientFactoryMock = new Mock<IHttpClientFactory>();
        httpClientFactoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));

        return new CostManagementClient(
            new FakeTokenCredential(),
            httpClientFactoryMock.Object,
            new NullLogger<CostManagementClient>());
    }

    private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, string content)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
    }

    private sealed class FakeTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return new AccessToken("fake-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(GetToken(requestContext, cancellationToken));
        }
    }

    private sealed class SequenceHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public int SendCount { get; private set; }

        public SequenceHttpMessageHandler(IEnumerable<HttpResponseMessage> responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            if (_responses.Count == 0)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            return Task.FromResult(_responses.Dequeue());
        }
    }
}
