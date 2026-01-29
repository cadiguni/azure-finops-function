namespace Gvdasa.GVmodeloexemploapi.Modelos.Entidades;

public class EntidadeExemplo : EntidadeMultiTenant
{
    public required string Nome { get; set; }
    public required string Descricao { get; set; }
    public required TipoExemplo Tipo { get; set; }
    public required string Identificador { get; init; }
    public required DateTimeOffset DataCriacao { get; init; }
    public required int? IdUsuarioCriacao { get; init; }
}
