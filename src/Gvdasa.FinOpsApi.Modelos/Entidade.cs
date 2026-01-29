using System.ComponentModel.DataAnnotations;

namespace Gvdasa.GVmodeloexemploapi.Modelos.Entidades;

public abstract class Entidade
{
    [Key]
    public Guid Id { get; init; } = Guid.NewGuid();
}
