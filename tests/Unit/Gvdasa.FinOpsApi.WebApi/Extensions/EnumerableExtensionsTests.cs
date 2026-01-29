using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Extensions.Tests;

[TestClass]
public class EnumerableExtensionsTests
{
    record Abc(string A, string B, string C);
    record Cde(string A, string B, string C) : Abc(A,B,C);
    record Fgh(string A, string B, string C) : Abc(A,B,C);

    [TestMethod]
    public void SortByProperty()
    {
        // arrange
        List<Abc> lista =
        [
            new Abc("qwerty", "qwerty", "qwerty"),
            new Abc("abcd", "abcd", "abcd"),
            new Abc("xyz", "xyz", "xyz"),
            new Abc("dfg", "dfg", "dfg"),
            new Abc("asdf", "asdf", "asdf"),
        ];

        // act
        IEnumerable<Abc> crescente = lista.SortByProperty("a");
        IEnumerable<Abc> decrescente = lista.SortByProperty("a", true);

        // assert
        Assert.AreEqual("abcd", crescente.ElementAt(0).A);
        Assert.AreEqual("asdf", crescente.ElementAt(1).A);
        Assert.AreEqual("dfg", crescente.ElementAt(2).A);
        Assert.AreEqual("qwerty", crescente.ElementAt(3).A);
        Assert.AreEqual("xyz", crescente.ElementAt(4).A);

        Assert.AreEqual("xyz", decrescente.ElementAt(0).A);
        Assert.AreEqual("qwerty", decrescente.ElementAt(1).A);
        Assert.AreEqual("dfg", decrescente.ElementAt(2).A);
        Assert.AreEqual("asdf", decrescente.ElementAt(3).A);
        Assert.AreEqual("abcd", decrescente.ElementAt(4).A);
    }

    [TestMethod]
    public void SortByFuncAndProperty()
    {
        // arrange
        List<Abc> lista =
        [
            new Fgh("qwerty", "qwerty", "qwerty"),
            new Fgh("abcd", "abcd", "abcd"),
            new Fgh("xyz", "xyz", "xyz"),
            new Cde("dfg", "dfg", "dfg"),
            new Cde("asdf", "asdf", "asdf"),
            new Cde("jkl", "jkl", "jkl"),
        ];

        // act
        IEnumerable<Abc> crescente = lista.SortByFuncAndProperty("a", x => x is Cde);
        IEnumerable<Abc> decrescente = lista.SortByFuncAndProperty("a", x => x is Cde, true);

        // assert
        Assert.AreEqual("abcd", crescente.ElementAt(0).A);
        Assert.AreEqual("qwerty", crescente.ElementAt(1).A);
        Assert.AreEqual("xyz", crescente.ElementAt(2).A);
        Assert.AreEqual("asdf", crescente.ElementAt(3).A);
        Assert.AreEqual("dfg", crescente.ElementAt(4).A);
        Assert.AreEqual("jkl", crescente.ElementAt(5).A);

        Assert.AreEqual("xyz", decrescente.ElementAt(0).A);
        Assert.AreEqual("qwerty", decrescente.ElementAt(1).A);
        Assert.AreEqual("abcd", decrescente.ElementAt(2).A);
        Assert.AreEqual("jkl", decrescente.ElementAt(3).A);
        Assert.AreEqual("dfg", decrescente.ElementAt(4).A);
        Assert.AreEqual("asdf", decrescente.ElementAt(5).A);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("inexistente")]
    public void SortByFuncAndProperty_propertyName_invalido(string propertyName)
    {
        // arrange
        List<Abc> lista =
        [
            new Fgh("qwerty", "qwerty", "qwerty"),
            new Fgh("abcd", "abcd", "abcd"),
            new Fgh("xyz", "xyz", "xyz"),
            new Cde("dfg", "dfg", "dfg"),
            new Cde("asdf", "asdf", "asdf"),
            new Cde("jkl", "jkl", "jkl"),
        ];

        // act
        var action = () => lista.SortByFuncAndProperty(propertyName, x => x is Cde);

        // assert
        Assert.ThrowsException<ArgumentException>(action);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("inexistente")]
    public void SortByProperty_propertyName_invalido(string propertyName)
    {
        // arrange
        List<Abc> lista =
        [
            new Fgh("qwerty", "qwerty", "qwerty"),
            new Fgh("abcd", "abcd", "abcd"),
            new Fgh("xyz", "xyz", "xyz"),
            new Cde("dfg", "dfg", "dfg"),
            new Cde("asdf", "asdf", "asdf"),
            new Cde("jkl", "jkl", "jkl"),
        ];

        // act
        var action = () => lista.SortByProperty(propertyName);

        // assert
        Assert.ThrowsException<ArgumentException>(action);
    }
}
