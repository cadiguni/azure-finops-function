using System.Diagnostics.CodeAnalysis;

namespace Gvdasa.GVmodeloexemploapi.Domain.Exceptions;

[Serializable]
[ExcludeFromCodeCoverage]
public class RegraDeNegocioException : Exception
{
    public RegraDeNegocioException(string message) : base(message) { }
    public RegraDeNegocioException(string message, Exception inner) : base(message, inner) { }
}
