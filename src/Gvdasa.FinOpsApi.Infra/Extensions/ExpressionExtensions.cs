using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Gvdasa.GVmodeloexemploapi.Infra.Extensions;

[ExcludeFromCodeCoverage]
public static class ExpressionExtensions
{
    // Método que combina expressões utilizando uma função de merge (AND ou OR)
    public static Expression<Func<T, bool>> CombineExpressions<T>
    (
        this IEnumerable<Expression<Func<T, bool>>> expressions,
        Func<Expression, Expression, BinaryExpression> merge
    )
    {
        var parameter = Expression.Parameter(typeof(T)); // Cria um parâmetro para a expressão
        Expression? body = null;

        // Itera sobre as expressões
        foreach (var expr in expressions)
        {
            var invokedExpr = Expression.Invoke(expr, parameter); // Invoca a expressão
            body = body == null ? invokedExpr : merge(body, invokedExpr); // Combina as expressões
        }

        return Expression.Lambda<Func<T, bool>>(body ?? Expression.Constant(true), parameter); // Retorna a expressão final
    }

    // Método de extensão para combinar usando AND
    public static Expression<Func<T, bool>> AndAlso<T>(this IEnumerable<Expression<Func<T, bool>>> expressions)
    {
        return expressions.CombineExpressions(Expression.AndAlso);
    }

    // Método de extensão para combinar usando OR
    public static Expression<Func<T, bool>> OrElse<T>(this IEnumerable<Expression<Func<T, bool>>> expressions)
    {
        return expressions.CombineExpressions(Expression.OrElse);
    }
}
