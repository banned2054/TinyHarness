using System.Xml.Linq;
using NUnit.Framework;

namespace TinyHarness.Desktop.Tests;

[TestFixture]
public sealed class ModelDiscoveryDialogLayoutTests
{
    [Test]
    public void ModelListScrollsInAConstrainedDialogWithSearchAndActionsOutsideViewport()
    {
        var        dialog   = LoadView("ModelDiscoveryDialogView.axaml");
        XNamespace controls = "https://github.com/avaloniaui";
        var        viewport = dialog.Descendants(controls + "ScrollViewer").Single();
        Assert.That((string?)viewport.Attribute("VerticalScrollBarVisibility"), Is.EqualTo("Visible"));
        Assert.That((string?)viewport.Attribute("AllowAutoHide"), Is.EqualTo("False"));
        Assert.That((string?)viewport.Attribute("HorizontalScrollBarVisibility"), Is.EqualTo("Disabled"));
        Assert.That((string?)viewport.Parent!.Attribute("Grid.Row"), Is.EqualTo("3"));
        Assert.That((string?)viewport.Parent.Parent!.Attribute("RowDefinitions"), Is.EqualTo("Auto,Auto,Auto,*,Auto"));
        Assert.That(dialog.Descendants(controls + "TextBox").Single().Ancestors(), Does.Not.Contain(viewport));
        Assert.That(dialog.Descendants(controls + "Button").Any(button => button.Ancestors().Contains(viewport)),
                    Is.False);
        Assert.That(dialog.Descendants(controls + "Popup"), Is.Empty);
        Assert.That(dialog.Descendants(controls + "ProgressBar"), Is.Empty);
        var loading = dialog.Descendants(controls + "TextBlock").Single(element =>
                                                                            (string?)element.Attribute("Text") ==
                                                                            "{Binding ModelListMessage}");
        Assert.That(loading.Parent, Is.SameAs(viewport.Parent));
        Assert.That((string?)loading.Attribute("HorizontalAlignment"), Is.EqualTo("Center"));
        Assert.That((string?)loading.Attribute("VerticalAlignment"), Is.EqualTo("Center"));
    }

    [Test]
    public void DiscoveryIsAboveTheModelListAndItsOverlayCoversAllSettings()
    {
        XNamespace controls = "https://github.com/avaloniaui";
        var catalog = LoadView("CatalogSettingsView.axaml");
        var elements = catalog.Descendants().ToList();
        var fetch = elements.Single(element => (string?)element.Attribute("Command") == "{Binding FetchModelsCommand}");
        var models = elements.Single(element => (string?)element.Attribute("ItemsSource") == "{Binding ModelEntries}");
        Assert.That(elements.IndexOf(fetch), Is.LessThan(elements.IndexOf(models)));
        var settings = LoadView("SettingsPanelView.axaml");
        var overlay = settings.Descendants(controls + "Grid").Single(element =>
                                                                         (string?)element.Attribute("IsVisible") ==
                                                                         "{Binding Catalog.IsModelPickerOpen}");
        var content = settings.Descendants(controls + "Grid").Single(element =>
                                                                         (string?)element.Attribute("IsEnabled") ==
                                                                         "{Binding !Catalog.IsModelPickerOpen}");
        Assert.That(overlay.Parent, Is.SameAs(content.Parent));
        Assert.That(overlay.Descendants().Any(element => element.Name.LocalName == "ModelDiscoveryDialogView"),
                    Is.True);
    }

    private static XDocument LoadView(string name)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TinyHarness.slnx"))) root = root.Parent;
        Assert.That(root, Is.Not.Null);
        return XDocument.Load(Path.Combine(root!.FullName, "TinyHarness.Desktop", "Presentation", "Views", name));
    }
}
