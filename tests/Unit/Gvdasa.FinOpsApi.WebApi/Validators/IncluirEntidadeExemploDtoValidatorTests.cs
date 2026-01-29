using System.Linq;
using FluentValidation.TestHelper;
using Gvdasa.GVmodeloexemploapi.WebApi.Dtos;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Validators.Tests;

[TestClass]
public class IncluirEntidadeExemploDtoValidatorTests : AtualizarEntidadeExemploDtoValidator<IncluirEntidadeExemploDto>
{
    IncluirEntidadeExemploDtoValidator validator = new();

    [TestMethod]
    [DataRow("TesteUm")]
    [DataRow("Testedois")]
    [DataRow("testeTres")]
    [DataRow("testequatro")]
    public void ValidacaoPassando(string valor)
    {
        // arrange
        IncluirEntidadeExemploDto dto = new()
        {
            Descricao = "Teste com espaço",
            Identificador = valor,
            Nome = "Teste com espaço",
            Tipo = Modelos.Entidades.TipoExemplo.Exemplo1,
        };

        // act
        var result = validator.TestValidate(dto);

        // assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow(".")]
    [DataRow("t")]
    [DataRow("Teste1")]
    [DataRow("Teste$")]
    [DataRow("Teste.")]
    [DataRow("Teste_teste")]
    [DataRow("Teste:teste")]
    [DataRow("Teste1teste")]
    [DataRow("Teste@teste")]
    [DataRow("Teste teste")]
    public void ValidacaoFalhando_identificador_invalido(string valor)
    {
        // arrange
        IncluirEntidadeExemploDto dto = new()
        {
            Descricao = "Teste com espaço",
            Identificador = valor,
            Nome = "Teste com espaço",
            Tipo = Modelos.Entidades.TipoExemplo.Exemplo1,
        };

        // act
        var result = validator.TestValidate(dto);

        // assert
        result.ShouldHaveValidationErrorFor(x => x.Identificador);
        Assert.IsFalse(result.Errors.Any(x => x.PropertyName != nameof(IncluirEntidadeExemploDto.Identificador)));
    }
}
