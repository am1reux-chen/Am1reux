using System.Xml.Linq;
using Xunit;

namespace Lexico.App.Tests;

public sealed class LibraryLayoutTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void NarrowHeaderMovesActionsAndFollowingContentToSeparateRows()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Production", "LibraryPage.xaml");
        var root = XDocument.Load(path).Root!;
        var wideHeader = root.Descendants().Single(element =>
            element.Name.LocalName == "VisualState" &&
            (string?)element.Attribute(XamlNamespace + "Name") == "WideLibraryHeader");
        Assert.Contains(wideHeader.Descendants(), element =>
            element.Name.LocalName == "AdaptiveTrigger" && (string?)element.Attribute("MinWindowWidth") == "760");

        var compactHeader = root.Descendants().SingleOrDefault(element =>
            element.Name.LocalName == "VisualState" &&
            (string?)element.Attribute(XamlNamespace + "Name") == "CompactLibraryHeader");

        Assert.NotNull(compactHeader);
        Assert.Contains(compactHeader!.Descendants(), element =>
            element.Name.LocalName == "AdaptiveTrigger" && (string?)element.Attribute("MinWindowWidth") == "0");

        var setters = compactHeader.Descendants()
            .Where(element => element.Name.LocalName == "Setter")
            .ToDictionary(element => (string)element.Attribute("Target")!, element => (string)element.Attribute("Value")!);

        Assert.Equal("1", setters["LibraryActions.(Grid.Row)"]);
        Assert.Equal("2", setters["SearchRoot.(Grid.Row)"]);
        Assert.Equal("3", setters["SearchStatus.(Grid.Row)"]);
        var content = root.Descendants().Single(element =>
            element.Name.LocalName == "Grid" && (string?)element.Attribute(XamlNamespace + "Name") == "LibraryContent");
        Assert.Equal("4", (string?)content.Attribute("Grid.Row"));

        var verticalActions = root.Descendants().SingleOrDefault(element =>
            element.Name.LocalName == "VisualState" &&
            (string?)element.Attribute(XamlNamespace + "Name") == "VerticalLibraryActions");
        Assert.NotNull(verticalActions);
        Assert.Contains(verticalActions!.Descendants(), element =>
            element.Name.LocalName == "AdaptiveTrigger" && (string?)element.Attribute("MinWindowWidth") == "0");
        Assert.Contains(verticalActions.Descendants(), element =>
            element.Name.LocalName == "Setter" &&
            (string?)element.Attribute("Target") == "LibraryActions.Orientation" &&
            (string?)element.Attribute("Value") == "Vertical");

        var horizontalActions = root.Descendants().Single(element =>
            element.Name.LocalName == "VisualState" &&
            (string?)element.Attribute(XamlNamespace + "Name") == "HorizontalLibraryActions");
        Assert.Contains(horizontalActions.Descendants(), element =>
            element.Name.LocalName == "AdaptiveTrigger" && (string?)element.Attribute("MinWindowWidth") == "500");
    }

    [Fact]
    public void ImportAndBackupStatusIsShownInSearchStatusArea()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Production", "LibraryPage.xaml");
        var root = XDocument.Load(path).Root!;
        var searchStatus = root.Descendants().Single(element =>
            element.Name.LocalName == "Grid" && (string?)element.Attribute(XamlNamespace + "Name") == "SearchStatus");

        Assert.Contains(searchStatus.Descendants(), element =>
            element.Name.LocalName == "TextBlock" &&
            ((string?)element.Attribute("Text"))?.Contains("Vm.StatusText", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void EmptyDeckHintNamesImportActionWithoutAssumingItsScreenPosition()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Production", "LibraryPage.xaml");
        var root = XDocument.Load(path).Root!;
        var emptyHint = root.Descendants().Single(element =>
            element.Name.LocalName == "StackPanel" &&
            (string?)element.Attribute(XamlNamespace + "Name") == "DecksEmptyHint");
        var guidance = emptyHint.Elements().Single(element =>
            element.Name.LocalName == "TextBlock" &&
            ((string?)element.Attribute("Text"))?.Contains("当前没有预装词库", StringComparison.Ordinal) == true);
        var guidanceText = (string)guidance.Attribute("Text")!;

        Assert.Contains("导入 CSV、TXT、TypeWords JSON 或 TypeWords GitHub ZIP 词书", guidanceText);
        Assert.DoesNotContain("右上角", guidanceText);

        var importButton = root.Descendants().Single(element =>
            element.Name.LocalName == "Button" && (string?)element.Attribute("Click") == "OnImportClick");
        Assert.Contains(importButton.Descendants(), element =>
            element.Name.LocalName == "TextBlock" && (string?)element.Attribute("Text") == "导入词库");
    }

    [Fact]
    public void SearchResultPronunciationWrapsBelowTheLemmaWithoutNarrowingItsColumn()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Production", "LibraryPage.xaml");
        var root = XDocument.Load(path).Root!;
        var lemma = root.Descendants().Single(element =>
            element.Name.LocalName == "TextBlock" &&
            ((string?)element.Attribute("Text"))?.Contains("Word.Lemma", StringComparison.Ordinal) == true);

        var wordDetails = lemma.Parent!;
        Assert.Equal("StackPanel", wordDetails.Name.LocalName);
        Assert.Equal("Wrap", (string?)lemma.Attribute("TextWrapping"));
        Assert.Equal("2", (string?)lemma.Attribute("MaxLines"));
        Assert.Equal("CharacterEllipsis", (string?)lemma.Attribute("TextTrimming"));

        var pronunciation = wordDetails.Elements().Single(element =>
            element.Name.LocalName == "TextBlock" &&
            ((string?)element.Attribute("Text"))?.Contains("PronunciationDisplay", StringComparison.Ordinal) == true);
        Assert.Equal("Wrap", (string?)pronunciation.Attribute("TextWrapping"));
        Assert.Equal("12", (string?)pronunciation.Attribute("FontSize"));
    }

    [Fact]
    public void SearchResultsCanExpandImportedWordDetails()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Production", "LibraryPage.xaml");
        var root = XDocument.Load(path).Root!;
        var expander = root.Descendants().Single(element => element.Name.LocalName == "Expander");

        Assert.Equal("{x:Bind Word.Details.HasContent, Mode=OneTime}", (string?)expander.Attribute("Visibility"));
        Assert.Contains(expander.Descendants(), element =>
            element.Name.LocalName == "ItemsControl" &&
            (string?)element.Attribute("ItemsSource") == "{x:Bind DetailSections}");
        Assert.Contains(expander.Descendants(), element =>
            element.Name.LocalName == "TextBlock" &&
            (string?)element.Attribute("Text") == "{x:Bind Primary}");
        Assert.Contains(expander.Descendants(), element =>
            element.Name.LocalName == "TextBlock" &&
            (string?)element.Attribute("Text") == "{x:Bind Secondary}");
    }

    [Fact]
    public void DeckLoadErrorShowsRetryActionInTheDecksPane()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Production", "LibraryPage.xaml");
        var root = XDocument.Load(path).Root!;
        var errorPanel = root.Descendants().SingleOrDefault(element =>
            element.Name.LocalName == "StackPanel" &&
            (string?)element.Attribute(XamlNamespace + "Name") == "DecksLoadErrorPanel");

        Assert.NotNull(errorPanel);
        Assert.Contains(errorPanel!.Descendants(), element =>
            element.Name.LocalName == "TextBlock" &&
            ((string?)element.Attribute("Text"))?.Contains("Vm.DecksLoadError", StringComparison.Ordinal) == true);
        Assert.Contains(errorPanel.Descendants(), element =>
            element.Name.LocalName == "Button" &&
            ((string?)element.Attribute("Command"))?.Contains("Vm.RetryDecksCommand", StringComparison.Ordinal) == true);
    }
}
