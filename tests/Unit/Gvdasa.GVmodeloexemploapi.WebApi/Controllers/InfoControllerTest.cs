using Gvdasa.GVmodeloexemploapi.WebApi.Controllers;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Gvdasa.GVsign.Unit.Controllers;

[TestClass]
public class InfoControllerTest
{
    [TestMethod("Obter versão")]
    public void ObterVersao()
    {
        var configMock = new Mock<IConfiguration>();
        configMock.Setup(cfg => cfg["AppInfo:Nome"]).Returns("Meu Produto");
        configMock.Setup(cfg => cfg["AppInfo:Descricao"]).Returns("Minha descrição");
        configMock.Setup(cfg => cfg["AppInfo:Time"]).Returns("Meu Time");

        var controller = new InfoController(configMock.Object);
        var informacao = controller.GetVersion();
        Assert.AreEqual("Meu Produto", informacao.App.Nome);
        Assert.AreEqual("Meu Time", informacao.App.Time);
        Assert.AreEqual("Minha descrição", informacao.App.Descricao);
        Assert.IsNotNull(informacao.App.Versao);
    }
}
