using System.Xml.Linq;
using Xunit;

namespace Lexico.App.Tests;

public sealed class ThemeResourceTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void DisabledControlOpacityUsesThemeResourceWithHighContrastOverride()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Production", "App.xaml");
        var root = XDocument.Load(path).Root!;
        var themeDictionaries = root.Descendants().Single(element => element.Name.LocalName == "ResourceDictionary.ThemeDictionaries");
        var dictionaries = themeDictionaries.Elements()
            .ToDictionary(element => (string)element.Attribute(XamlNamespace + "Key")!, element => element);

        Assert.Equal("0.45", ResourceValue(dictionaries["Default"], "LexioDisabledOpacity"));
        Assert.Equal("0.45", ResourceValue(dictionaries["Light"], "LexioDisabledOpacity"));
        Assert.Equal("1", ResourceValue(dictionaries["HighContrast"], "LexioDisabledOpacity"));

        var opacitySetters = root.Descendants().Where(element =>
            element.Name.LocalName == "Setter" && (string?)element.Attribute("Target") == "Root.Opacity").ToList();
        Assert.Equal(2, opacitySetters.Count);
        Assert.All(opacitySetters, setter =>
            Assert.Equal("{ThemeResource LexioDisabledOpacity}", (string?)setter.Attribute("Value")));
    }

    [Fact]
    public void LearnPauseScrimUsesThemeAwareBrush()
    {
        var appPath = Path.Combine(AppContext.BaseDirectory, "Production", "App.xaml");
        var appRoot = XDocument.Load(appPath).Root!;
        var themeDictionaries = appRoot.Descendants()
            .Single(element => element.Name.LocalName == "ResourceDictionary.ThemeDictionaries");
        var dictionaries = themeDictionaries.Elements()
            .ToDictionary(element => (string)element.Attribute(XamlNamespace + "Key")!, element => element);

        Assert.Equal("#66000000", ResourceColor(dictionaries["Default"], "LexioPauseScrimBrush"));
        Assert.Equal("#66000000", ResourceColor(dictionaries["Light"], "LexioPauseScrimBrush"));
        Assert.Equal("{ThemeResource SystemColorWindowColor}", ResourceColor(dictionaries["HighContrast"], "LexioPauseScrimBrush"));

        var learnPath = Path.Combine(AppContext.BaseDirectory, "Production", "LearnPage.xaml");
        var learnRoot = XDocument.Load(learnPath).Root!;
        var pauseOverlay = learnRoot.Descendants().Single(element =>
            (string?)element.Attribute(XamlNamespace + "Name") == "PauseOverlay");
        Assert.Equal("{ThemeResource LexioPauseScrimBrush}", (string?)pauseOverlay.Attribute("Background"));
    }

    private static string ResourceValue(XElement dictionary, string key) => dictionary.Descendants()
        .Single(element => (string?)element.Attribute(XamlNamespace + "Key") == key)
        .Value.Trim();

    private static string ResourceColor(XElement dictionary, string key) => (string)dictionary.Descendants()
        .Single(element => (string?)element.Attribute(XamlNamespace + "Key") == key)
        .Attribute("Color")!;
}
