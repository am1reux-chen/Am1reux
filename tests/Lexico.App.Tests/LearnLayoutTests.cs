using System.Xml.Linq;
using Xunit;

namespace Lexico.App.Tests;

public sealed class LearnLayoutTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void CompletionSummaryHasNamedReturnButtonForFocusRestoration()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Production", "LearnPage.xaml");
        var root = XDocument.Load(path).Root!;
        var summary = root.Descendants().Single(element =>
            element.Name.LocalName == "StackPanel" &&
            element.Elements().Any(child =>
                child.Name.LocalName == "TextBlock" && (string?)child.Attribute("Text") == "学习总结"));

        var returnButton = summary.Descendants().Single(element =>
            element.Name.LocalName == "Button" &&
            (string?)element.Attribute(XamlNamespace + "Name") == "ReturnToTodayButton");

        Assert.Equal("OnBackToToday", (string?)returnButton.Attribute("Click"));
    }

    [Fact]
    public void KeyboardHintBindsToCurrentGradeMode()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Production", "LearnPage.xaml");
        var root = XDocument.Load(path).Root!;

        Assert.Contains(root.Descendants(), element =>
            element.Name.LocalName == "TextBlock" &&
            ((string?)element.Attribute("Text"))?.Contains("Vm.KeyboardHint", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void KeyboardHintWrapsInNarrowWindows()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Production", "LearnPage.xaml");
        var root = XDocument.Load(path).Root!;
        var keyboardHint = root.Descendants().Single(element =>
            element.Name.LocalName == "TextBlock" &&
            ((string?)element.Attribute("Text"))?.Contains("Vm.KeyboardHint", StringComparison.Ordinal) == true);

        Assert.Equal("Wrap", (string?)keyboardHint.Attribute("TextWrapping"));
    }

    [Fact]
    public void LemmaUsesRemainingWidthBetweenBadgeAndPronunciationButton()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Production", "LearnPage.xaml");
        var root = XDocument.Load(path).Root!;
        var lemma = root.Descendants().Single(element =>
            element.Name.LocalName == "TextBlock" &&
            ((string?)element.Attribute("Text"))?.Contains("Vm.Lemma", StringComparison.Ordinal) == true);

        Assert.Equal("1", (string?)lemma.Attribute("Grid.Column"));
        var header = lemma.Parent!;
        Assert.Equal("Grid", header.Name.LocalName);
        Assert.Equal(new[] { "Auto", "*", "Auto" }, header.Elements()
            .Where(element => element.Name.LocalName == "Grid.ColumnDefinitions")
            .Single().Elements()
            .Select(element => (string?)element.Attribute("Width")));
        Assert.Contains(header.Elements(), element =>
            element.Name.LocalName == "Border" && (string?)element.Attribute("Grid.Column") == "0");
        Assert.Contains(header.Elements(), element =>
            element.Name.LocalName == "Button" && (string?)element.Attribute("Grid.Column") == "2");
    }

    [Fact]
    public void PauseButtonUsesTheToggleCommandAndStatefulAccessibleName()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Production", "LearnPage.xaml");
        var root = XDocument.Load(path).Root!;
        var pauseButton = root.Descendants().Single(element =>
            element.Name.LocalName == "Button" &&
            (string?)element.Attribute(XamlNamespace + "Name") == "PauseButton");

        Assert.Contains("Vm.PauseCommand", (string?)pauseButton.Attribute("Command"));
        Assert.Contains("Vm.PauseButtonAccessibleName", (string?)pauseButton.Attribute("AutomationProperties.Name"));
    }

    [Fact]
    public void PauseShortcutHasOneTargetWhileTheOverlayIsVisible()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Production", "LearnPage.xaml");
        var root = XDocument.Load(path).Root!;

        Assert.Single(root.Descendants(), element =>
            element.Name.LocalName == "KeyboardAccelerator" &&
            (string?)element.Attribute("Key") == "P");
    }

    [Fact]
    public void KeyboardShortcutsAreScopedToTheLearnPage()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Production", "LearnPage.xaml");
        var root = XDocument.Load(path).Root!;
        var shortcuts = root.Descendants()
            .Where(element => element.Name.LocalName == "KeyboardAccelerator")
            .ToList();

        Assert.Contains(root.Descendants(), element =>
            element.Name.LocalName == "Grid" && (string?)element.Attribute(XamlNamespace + "Name") == "LearnRoot");
        Assert.NotEmpty(shortcuts);
        Assert.All(shortcuts, shortcut =>
            Assert.Equal("{x:Bind LearnRoot}", (string?)shortcut.Attribute("ScopeOwner")));
    }

    [Fact]
    public void GradeShortcutsAreEnabledOnlyForTheirGradeMode()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Production", "LearnPage.xaml");
        var root = XDocument.Load(path).Root!;
        var fourGradeGrid = root.Descendants().Single(element =>
            element.Name.LocalName == "Grid" && (string?)element.Attribute("ColumnSpacing") == "8");
        var twoGradeGrid = root.Descendants().Single(element =>
            element.Name.LocalName == "Grid" && (string?)element.Attribute("ColumnSpacing") == "12");

        AssertGradeShortcuts(fourGradeGrid, ["Number1", "Number2", "Number3", "Number4"],
            "{x:Bind Vm.FourGradeShortcutsEnabled, Mode=OneWay}");
        AssertGradeShortcuts(twoGradeGrid, ["Number1", "Number3"],
            "{x:Bind Vm.SimpleGradeShortcutsEnabled, Mode=OneWay}");
    }

    [Fact]
    public void MissingMeaningShowsAChineseMessageAndHidesGradeOptions()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Production", "LearnPage.xaml");
        var root = XDocument.Load(path).Root!;
        Assert.Contains(root.Descendants(), element =>
            element.Name.LocalName == "TextBlock" &&
            (string?)element.Attribute("Text") == "这张卡片没有释义，不能评分。请结束本次学习，补充词义后重新开始。" &&
            (string?)element.Attribute("Visibility") == "{x:Bind Vm.ShowMissingMeaning, Mode=OneWay}");
        Assert.Contains(root.Descendants(), element =>
            element.Name.LocalName == "Grid" &&
            (string?)element.Attribute("Visibility") == "{x:Bind Vm.ShowGradeOptions, Mode=OneWay}");
    }

    [Fact]
    public void PronunciationShortcutIsEnabledOnlyWhileACardIsVisible()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Production", "LearnPage.xaml");
        var root = XDocument.Load(path).Root!;
        var shortcut = root.Descendants().Single(element =>
            element.Name.LocalName == "KeyboardAccelerator" && (string?)element.Attribute("Key") == "E");

        Assert.Equal("{x:Bind Vm.SpeakShortcutEnabled, Mode=OneWay}", (string?)shortcut.Attribute("IsEnabled"));
    }

    [Fact]
    public void LearnPageBindsTheDisplayTextForBothPronunciations()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Production", "LearnPage.xaml");
        var root = XDocument.Load(path).Root!;
        var pronunciation = root.Descendants().Single(element =>
            element.Name.LocalName == "TextBlock" &&
            ((string?)element.Attribute("Text"))?.Contains("Vm.IpaDisplay", StringComparison.Ordinal) == true);

        Assert.Equal("{x:Bind Vm.ShowIpa, Mode=OneWay}", (string?)pronunciation.Attribute("Visibility"));
    }

    private static void AssertGradeShortcuts(XElement gradeGrid, string[] expectedKeys, string expectedIsEnabled)
    {
        var shortcuts = gradeGrid.Descendants()
            .Where(element => element.Name.LocalName == "KeyboardAccelerator")
            .ToList();

        Assert.Equal(expectedKeys, shortcuts.Select(element => (string?)element.Attribute("Key")));
        Assert.All(shortcuts, shortcut =>
            Assert.Equal(expectedIsEnabled, (string?)shortcut.Attribute("IsEnabled")));
    }
}
