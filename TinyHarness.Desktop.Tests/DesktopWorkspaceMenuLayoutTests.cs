using NUnit.Framework;
using System.Xml.Linq;

namespace TinyHarness.Desktop.Tests;

[TestFixture]
public sealed class DesktopWorkspaceMenuLayoutTests
{
    [Test]
    public void ProjectMenuScrollsOnlyItsListAndKeepsTheDirectoryEntryOutsideTheViewport()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TinyHarness.slnx"))) root = root.Parent;
        Assert.That(root, Is.Not.Null, "未找到包含界面源码的仓库根目录。");
        var document = XDocument.Load(Path.Combine(root!.FullName, "TinyHarness.Desktop", "Presentation", "Views",
                                                   "MainWindow.axaml"));
        XNamespace controls = "https://github.com/avaloniaui";
        var list = document.Descendants(controls + "ItemsControl")
                           .Single(element => (string?)element.Attribute("ItemsSource") ==
                                              "{Binding ProjectMenuOptions}");
        var viewport = list.Parent;
        Assert.That(viewport?.Name, Is.EqualTo(controls + "ScrollViewer"),
                    "项目列表必须具有独立滚动视口，ItemsControl 限高不能替代滚动。");
        Assert.That((double?)viewport!.Attribute("MaxHeight"), Is.EqualTo(288));
        Assert.That((string?)viewport.Attribute("HorizontalScrollBarVisibility"), Is.EqualTo("Disabled"));
        Assert.That(list.Attribute("MaxHeight"), Is.Null);
        var popup = viewport.Ancestors(controls + "Popup").Single();
        Assert.That((string?)popup.Attribute("Placement"), Is.EqualTo("TopEdgeAlignedLeft"));
        var directoryEntry = popup.Descendants(controls + "Button")
                                  .Single(element => (string?)element.Attribute("Click") == "BrowseFolder");
        Assert.That(directoryEntry.Ancestors(), Does.Not.Contain(viewport), "打开目录入口应固定在滚动列表之外。");
        Assert.That(directoryEntry.Parent, Is.SameAs(viewport.Parent));
    }
}
