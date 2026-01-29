using System.Reflection;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Extensions;

public static class EnumerableExtensions
{
    public static IEnumerable<T> SortByProperty<T>(this IEnumerable<T> list, string propertyName, bool decrescente = false)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            throw new ArgumentException("A propriedade deve ser especificada.", nameof(propertyName));
        }

        PropertyInfo? propInfo = typeof(T).GetProperty(propertyName, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
        if (propInfo == null)
        {
            throw new ArgumentException("A propriedade especificada não existe na classe.", nameof(propertyName));
        }

        return decrescente
            ? list.OrderByDescending(x => propInfo.GetValue(x))
            : list.OrderBy(x => propInfo.GetValue(x));
    }

    public static IEnumerable<T> SortByFuncAndProperty<T>(this IEnumerable<T> list, string propertyName, Func<T, bool> func, bool decrescente = false)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            throw new ArgumentException("A propriedade deve ser especificada.", nameof(propertyName));
        }

        PropertyInfo? propInfo = typeof(T).GetProperty(propertyName, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
        if (propInfo == null)
        {
            throw new ArgumentException("A propriedade especificada não existe na classe.", nameof(propertyName));
        }

        return decrescente
            ? list.OrderBy(func).ThenByDescending(x => propInfo.GetValue(x))
            : list.OrderBy(func).ThenBy(x => propInfo.GetValue(x));
    }
}
