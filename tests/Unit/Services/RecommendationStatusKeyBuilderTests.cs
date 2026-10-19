using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Services;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class RecommendationStatusKeyBuilderTests
{
    [Fact]
    public void Build_ShouldBeDeterministicAndCaseInsensitive()
    {
        var key1 = RecommendationStatusKeyBuilder.Build("/subscriptions/abc/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm01", "IdleVm");
        var key2 = RecommendationStatusKeyBuilder.Build("/SUBSCRIPTIONS/ABC/resourcegroups/rg/providers/Microsoft.Compute/virtualMachines/vm01", "idlevm");

        key1.Should().Be(key2);
    }
}
