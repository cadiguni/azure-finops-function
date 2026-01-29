using System.Diagnostics.CodeAnalysis;

namespace Gvdasa.GVmodeloexemploapi.Infra.Exceptions;

[System.Serializable]
[ExcludeFromCodeCoverage]
public class RecursoNaoEncontradoException : System.Exception
{
    public RecursoNaoEncontradoException(string message) : base(message) { }
    public RecursoNaoEncontradoException(string message, System.Exception inner) : base(message, inner) { }
}
