using System.Security.Cryptography;
using System.Text;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public static class RecommendationStatusKeyBuilder
{
    public static string Build(string resourceId, string recommendationType)
    {
        var normalized = $"{resourceId?.Trim().ToLowerInvariant()}|{recommendationType?.Trim().ToLowerInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
