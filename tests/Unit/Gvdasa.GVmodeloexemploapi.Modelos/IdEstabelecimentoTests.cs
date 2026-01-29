using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Gvdasa.GVmodeloexemploapi.Modelos.Tests;

[TestClass]
public class IdEstabelecimentoTests
{
    [TestMethod]
    [DataRow("1")]
    [DataRow("1.1")]
    [DataRow("12.3")]
    [DataRow("1.23")]
    [DataRow("11.23")]
    [DataRow("11")]
    [DataRow("12.34.56")]
    [DataRow("1.3.5")]
    [DataRow("1.3.55")]
    [DataRow("12.3.55")]
    [DataRow("1.2")]
    [DataRow("12.34")]
    [DataRow("2.34.56")]
    [DataRow("2.3")]
    [DataRow("1.11")]
    [DataRow("11.1")]
    [DataRow("11.22")]
    [DataRow("12.12.12")]
    [DataRow("12.34.5")]
    [DataRow("1.23.45")]
    [DataRow("23")]
    [DataRow("2.34")]
    [DataRow("11.2")]
    [DataRow("10.11")]
    [DataRow("9.87")]
    [DataRow("1.22.3")]
    public void IdValido(string valor)
    {
        try
        {
            IdEstabelecimento id = new(valor);
            IdEstabelecimento id2 = valor;
            Assert.AreEqual(id, id2);
            Assert.IsTrue(id == valor);
            Assert.IsTrue(id.Equals(valor));
            Assert.IsTrue(valor != "1.21");
            Assert.IsTrue(valor != new IdEstabelecimento("1.21"));
            object obj = new string(valor);
            object obj2 = new IdEstabelecimento(valor);
            Assert.IsTrue(id.Equals(obj));
            Assert.IsTrue(id.Equals(obj2));
        }
        catch (Exception)
        {
            Assert.Fail("Exceção não esperada para o valor " + valor);
        }
    }

    [TestMethod]
    [DataRow("00")]
    [DataRow("00.1")]
    [DataRow("1.00")]
    [DataRow("1.12.00")]
    [DataRow("1.12.12.1")]
    [DataRow("1.1.1.1")]
    [DataRow("0")]
    [DataRow("0.1")]
    [DataRow("1.0")]
    [DataRow("1.01.02")]
    [DataRow("123")]
    [DataRow("1..2")]
    [DataRow("1.2.3.4")]
    [DataRow("12.34.")]
    [DataRow(".1")]
    [DataRow("12.34..56")]
    [DataRow("12.34.5.")]
    [DataRow("12345678")]
    [DataRow("12.3456")]
    public void IdInvalido(string valor)
    {
        Assert.ThrowsException<ArgumentException>(() => new IdEstabelecimento(valor));
    }
}
