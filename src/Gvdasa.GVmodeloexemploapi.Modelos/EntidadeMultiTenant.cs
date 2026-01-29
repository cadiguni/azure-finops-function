namespace Gvdasa.GVmodeloexemploapi.Modelos.Entidades;

public abstract class EntidadeMultiTenant : Entidade
{
    public required string IdTenant { get; init; }
}
