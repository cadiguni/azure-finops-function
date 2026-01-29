using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;

namespace Gvdasa.GVmodeloexemploapi.Infra.Commands;

public interface IEntidadeExemploDados
{
    public Guid? Id { get; } // id existe no update, mas não na criação
    public string Nome { get; }
    public string? Identificador { get; }
    public string Descricao { get; }
    public TipoExemplo Tipo { get; }
}
