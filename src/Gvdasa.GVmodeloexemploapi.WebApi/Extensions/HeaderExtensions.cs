using System.Diagnostics.CodeAnalysis;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Extensions;

[ExcludeFromCodeCoverage]
public static class HeaderExtensions
{
    [ExcludeFromCodeCoverage]
    public static string? GetValueOrDefault(this IHeaderDictionary dictionary, string key)
    {
        return dictionary.TryGetValue(key, out var stringValues)
            ? stringValues.FirstOrDefault()
            : null;
    }
}
