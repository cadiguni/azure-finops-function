using System.Diagnostics.CodeAnalysis;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Dtos;

[ExcludeFromCodeCoverage]
public record EstabelecimentoInfo
{
    public required Guid Id {get; init; }
    public required string Codigo {get; init; }
    public required string CodigoEstruturado {get; init; }
    public required string NomeExibicao {get; init; }
    public IEnumerable<EstabelecimentoComSetoresInfo> Estabelecimentos { get; init; } = new List<EstabelecimentoComSetoresInfo>();


    public IEnumerable<SetorResumoInfo> ObterTodosSetores()
    {
        return ObterEstabelecimentosDesencadeados().OfType<EstabelecimentoComSetoresInfo>().SelectMany(x => x.Setores);
    }

    public virtual IEnumerable<EstabelecimentoInfo> ObterEstabelecimentosDesencadeados()
    {
        return new List<EstabelecimentoInfo> { this }.Concat(Estabelecimentos.SelectMany(x => x.ObterEstabelecimentosDesencadeados()));
    }
}
