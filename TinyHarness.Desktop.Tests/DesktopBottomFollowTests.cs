using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using NUnit.Framework;
using System.Xml.Linq;
using TinyHarness.Desktop.Presentation.Views.Conversation;

namespace TinyHarness.Desktop.Tests;

[TestFixture]
[NonParallelizable]
[SingleThreaded]
public sealed class DesktopBottomFollowTests
{
    [TearDown]
    public void DrainLayoutCallbacks() => Dispatcher.UIThread.RunJobs();

    [Test]
    public void ContentGrowthKeepsTheBottomVisibleAcrossSuccessiveLayoutPasses()
    {
        var viewer = CreateViewer(150);
        Assert.That(viewer.Offset.Y, Is.Zero);

        SetGeometry(viewer, 800);
        Flush();
        Assert.That(viewer.Offset.Y, Is.EqualTo(500));

        SetGeometry(viewer, 920);
        SetGeometry(viewer, 1000);
        Flush();
        Assert.That(viewer.Offset.Y, Is.EqualTo(700));
    }

    [Test]
    public void ScrollingUpPausesFollowingUntilTheUserReturnsToTheBottom()
    {
        var viewer = CreateViewer(800);
        viewer.Offset = new Vector(0, 200);
        SetGeometry(viewer, 900);
        Flush();
        Assert.That(viewer.Offset.Y, Is.EqualTo(200));

        viewer.Offset = new Vector(0, 400);
        SetGeometry(viewer, 1000);
        Flush();
        Assert.That(viewer.Offset.Y, Is.EqualTo(400));

        viewer.Offset = new Vector(0, 700);
        SetGeometry(viewer, 1100);
        Flush();
        Assert.That(viewer.Offset.Y, Is.EqualTo(800));
    }

    [Test]
    public void ScrollingUpCancelsAnAlreadyQueuedFollow()
    {
        var viewer = CreateViewer(800);
        SetGeometry(viewer, 1000);
        viewer.Offset = new Vector(0, 250);
        Flush();
        Assert.That(viewer.Offset.Y, Is.EqualTo(250));
    }

    [Test]
    public void ScrollingPartwayDownBeforeAPendingFollowKeepsTheChosenPosition()
    {
        var viewer = CreateViewer(800);
        SetGeometry(viewer, 1000);
        viewer.Offset = new Vector(0, 550);
        Flush();
        Assert.That(viewer.Offset.Y, Is.EqualTo(550));

        SetGeometry(viewer, 1100);
        Flush();
        Assert.That(viewer.Offset.Y, Is.EqualTo(550));
    }

    [Test]
    public void ViewportChangesFollowOnlyWhileAtTheBottom()
    {
        var viewer = CreateViewer(1000);
        SetGeometry(viewer, 1000, 200);
        Flush();
        Assert.That(viewer.Offset.Y, Is.EqualTo(800));

        SetGeometry(viewer, 1000, 400);
        Flush();
        Assert.That(viewer.Offset.Y, Is.EqualTo(600));

        viewer.Offset = new Vector(0, 200);
        SetGeometry(viewer, 1000, 250);
        Flush();
        Assert.That(viewer.Offset.Y, Is.EqualTo(200));
    }

    [Test]
    public void CollapsingContentDoesNotDisableFollowingWhenTheOffsetIsClamped()
    {
        var viewer = CreateViewer(1000);
        SetGeometry(viewer, 500);
        Flush();
        Assert.That(viewer.Offset.Y, Is.EqualTo(200));

        SetGeometry(viewer, 900);
        Flush();
        Assert.That(viewer.Offset.Y, Is.EqualTo(600));
    }

    [Test]
    public void FractionalPixelRoundingStillCountsAsTheBottom()
    {
        var viewer = CreateViewer(800);
        viewer.Offset = new Vector(0, 499.5);
        SetGeometry(viewer, 900);
        Flush();
        Assert.That(viewer.Offset.Y, Is.EqualTo(600));
    }

    [Test]
    public void ChangingTheSessionResetsThePreviousSessionsPausedState()
    {
        var viewer = CreateViewer(800);
        viewer.DataContext = new object();
        Flush();
        viewer.Offset = new Vector(0, 200);

        viewer.DataContext = new object();
        Flush();
        Assert.That(viewer.Offset.Y, Is.EqualTo(500));

        SetGeometry(viewer, 900);
        Flush();
        Assert.That(viewer.Offset.Y, Is.EqualTo(600));
    }

    [Test]
    public void ReasoningAndTimelineKeepIndependentFollowStates()
    {
        var timeline  = CreateViewer(1000);
        var reasoning = CreateViewer(800);
        timeline.Content = reasoning;
        timeline.Offset  = new Vector(0, 200);

        SetGeometry(reasoning, 1000);
        SetGeometry(timeline, 1200);
        Flush();
        Assert.That(reasoning.Offset.Y, Is.EqualTo(700));
        Assert.That(timeline.Offset.Y, Is.EqualTo(200));

        reasoning.Offset = new Vector(0, 100);
        timeline.Offset  = new Vector(0, 900);
        SetGeometry(reasoning, 1200);
        SetGeometry(timeline, 1400);
        Flush();
        Assert.That(reasoning.Offset.Y, Is.EqualTo(100));
        Assert.That(timeline.Offset.Y, Is.EqualTo(1100));
    }

    [Test]
    public void VerticalFollowingPreservesTheHorizontalReadingPosition()
    {
        var viewer = CreateViewer(800);
        viewer.Offset = new Vector(40, 500);
        SetGeometry(viewer, 900);
        Flush();
        Assert.That(viewer.Offset, Is.EqualTo(new Vector(40, 600)));
        Assert.That(viewer.StyleKey, Is.EqualTo(typeof(ScrollViewer)));
    }

    [Test]
    public void TimelineAndReasoningViewsUseTheFollowControl()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TinyHarness.slnx"))) root = root.Parent;
        Assert.That(root, Is.Not.Null);
        var        views        = Path.Combine(root!.FullName, "TinyHarness.Desktop", "Presentation", "Views");
        XNamespace conversation = "using:TinyHarness.Desktop.Presentation.Views.Conversation";
        var timeline = XDocument.Load(Path.Combine(views, "MainWindow.axaml"))
                                .Descendants(conversation + nameof(BottomFollowScrollViewer)).Single();
        Assert.That((string?)timeline.Attribute("Grid.Row"), Is.EqualTo("1"));
        var reasoning = XDocument.Load(Path.Combine(views, "Conversation", "MessageView.axaml"))
                                 .Descendants(conversation + nameof(BottomFollowScrollViewer)).Single();
        Assert.That((string?)reasoning.Attribute("IsVisible"), Is.EqualTo("{Binding IsReasoningExpanded}"));
    }

    private static BottomFollowScrollViewer CreateViewer(double height)
    {
        var viewer = new BottomFollowScrollViewer();
        SetGeometry(viewer, height);
        Flush();
        return viewer;
    }

    /// <summary>
    ///     <para>模拟布局发布视口尺寸，驱动真实 Avalonia 属性通知与偏移量约束；不启动窗口。</para>
    ///     <para>
    ///         Simulates layout publishing viewport metrics to exercise real Avalonia notifications and offset coercion
    ///         without a window.
    ///     </para>
    /// </summary>
    private static void SetGeometry(ScrollViewer viewer, double height, double viewportHeight = 300)
    {
        typeof(ScrollViewer).GetProperty(nameof(ScrollViewer.Extent))!.SetValue(viewer, new Size(600, height));
        typeof(ScrollViewer).GetProperty(nameof(ScrollViewer.Viewport))!
                            .SetValue(viewer, new Size(300, viewportHeight));
    }

    private static void Flush() => Dispatcher.UIThread.RunJobs();
}
