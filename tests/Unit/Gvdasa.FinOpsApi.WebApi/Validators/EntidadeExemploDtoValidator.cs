using System.Linq;
using FluentValidation.TestHelper;
using Gvdasa.GVmodeloexemploapi.WebApi.Dtos;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Validators.Tests;

[TestClass]
public class AtualziarEntidadeExemploDtoValidatorTests
{
    AtualizarEntidadeExemploDtoValidator validator = new();

    [TestMethod]
    public void ValidacaoPassando()
    {
        // arrange
        AtualizarEntidadeExemploDto dto = new()
        {
            Descricao = "Teste 1",
            Nome = "Teste 1",
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
    public void ValidacaoFalhando_nome_invalido(string valor)
    {
        // arrange
        AtualizarEntidadeExemploDto dto = new()
        {
            Descricao = "Teste com espaço",
            Nome = valor,
            Tipo = Modelos.Entidades.TipoExemplo.Exemplo1,
        };

        // act
        var result = validator.TestValidate(dto);

        // assert
        result.ShouldHaveValidationErrorFor(x => x.Nome);
        Assert.IsFalse(result.Errors.Any(x => x.PropertyName != nameof(AtualizarEntidadeExemploDto.Nome)));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow(".")]
    [DataRow("t")]
    public void ValidacaoFalhando_descricao_invalida(string valor)
    {
        // arrange
        AtualizarEntidadeExemploDto dto = new()
        {
            Descricao = valor,
            Nome = "Nome do exemplo",
            Tipo = Modelos.Entidades.TipoExemplo.Exemplo1,
        };

        // act
        var result = validator.TestValidate(dto);

        // assert
        result.ShouldHaveValidationErrorFor(x => x.Descricao);
        Assert.IsFalse(result.Errors.Any(x => x.PropertyName != nameof(AtualizarEntidadeExemploDto.Descricao)));
    }

    [TestMethod]
    public void ValidacaoFalhando_tipo_invalido()
    {
        // arrange
        AtualizarEntidadeExemploDto dto = new()
        {
            Descricao = "Teste de descrição",
            Nome = "Nome do exemplo",
            Tipo = Modelos.Entidades.TipoExemplo.Indefinido,
        };

        // act
        var result = validator.TestValidate(dto);

        // assert
        result.ShouldHaveValidationErrorFor(x => x.Tipo);
        Assert.IsFalse(result.Errors.Any(x => x.PropertyName != nameof(AtualizarEntidadeExemploDto.Tipo)));
    }
}
