using FluentValidation;
using Gvdasa.GVmodeloexemploapi.WebApi.Dtos;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Validators;

public class AtualizarEntidadeExemploDtoValidator<T> : AbstractValidator<T> where T : AtualizarEntidadeExemploDto
{
    // descrição
    private const int NomeTamanhoMin = 2;
    private const int NomeTamanhoMax = 100;

    // descrição
    private const int DescricaoTamanhoMin = 2;
    private const int DescricaoTamanhoMax = 250;

    public AtualizarEntidadeExemploDtoValidator()
    {
        RuleFor(x => x.Descricao)
            .NotEmpty()
                .WithMessage("Descrição deve ser definida")
            .MinimumLength(DescricaoTamanhoMin)
                .WithMessage($"Descrição deve possuir {DescricaoTamanhoMin} ou mais caracteres")
            .MaximumLength(DescricaoTamanhoMax)
                .WithMessage($"Descrição deve possuir menos de {DescricaoTamanhoMax} caracteres");

        RuleFor(x => x.Nome)
            .NotEmpty()
                .WithMessage("Nome deve ser definido")
            .MinimumLength(NomeTamanhoMin)
                .WithMessage($"Nome deve possuir {NomeTamanhoMin} ou mais caracteres")
            .MaximumLength(NomeTamanhoMax)
                .WithMessage($"Nome deve possuir menos de {NomeTamanhoMax} caracteres");

        RuleFor(x => x.Tipo)
            .IsInEnum()
                .WithMessage("Tipo deve possuir valor válido")
            .NotEqual(Modelos.Entidades.TipoExemplo.Indefinido)
                .WithMessage("Tipo deve possuir valor válido");
    }
}


public class AtualizarEntidadeExemploDtoValidator : AtualizarEntidadeExemploDtoValidator<AtualizarEntidadeExemploDto> { }
