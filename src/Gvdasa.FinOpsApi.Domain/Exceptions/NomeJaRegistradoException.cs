using System.Diagnostics.CodeAnalysis;

namespace Gvdasa.GVmodeloexemploapi.Domain.Exceptions;

[Serializable]
[ExcludeFromCodeCoverage]
public class NomeJaRegistradoException(string nome)
    : RegraDeNegocioException($"Já existe registro com o nome {nome}")
{
}
