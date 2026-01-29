using FluentValidation;
using Gvdasa.GVmodeloexemploapi.WebApi.Dtos;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Validators;

public class IncluirEntidadeExemploDtoValidator : AtualizarEntidadeExemploDtoValidator<IncluirEntidadeExemploDto>
{
    private const int IdentificadorTamanhoMin = 2;
    private const int IdentificadorTamanhoMax = 250;
    private const string IdentificadorRegex = @"^[a-zA-Z]+$";

    public IncluirEntidadeExemploDtoValidator()
    {
        RuleFor(x => x.Identificador)
            .MinimumLength(IdentificadorTamanhoMin)
                .WithMessage($"Identificador deve possuir {IdentificadorTamanhoMin} ou mais caracteres")
            .MaximumLength(IdentificadorTamanhoMax)
                .WithMessage($"Identificador deve possuir menos de {IdentificadorTamanhoMax} caracteres")
            .Matches(IdentificadorRegex)
                .WithMessage($"Identificador deve possuir apenas letras");
    }
}
