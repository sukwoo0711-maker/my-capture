using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Regression tests for <see cref="VideoLayerTimeline"/>.
/// Verifies mutable collection mutation tracking, row geometry and selection.
/// </summary>
public sealed class VideoLayerTimelineRegressionTests
{
    [Fact]
    public void VideoLayerTimeline_SameMutableListInstance_GainingAndRemovingLayers_UpdatesHeightAndSelection() =>
        StaTestHost.Run(() =>
        {
            var timeline = new VideoLayerTimeline();
            var textList = new List<TimedTextOverlay>();
            var frameList = new List<FrameEditLayer>();

            timeline.SetLayers(textList, frameList);
            Assert.Equal(56, timeline.Height);
            Assert.Null(timeline.SelectedId);

            // 1. Same mutable text list gains first layer
            var t1 = new TimedTextOverlay { Text = "Layer 1", StartMs = 0, EndMs = 1000 };
            textList.Add(t1);
            timeline.SetLayers(textList, frameList);
            Assert.Equal(62, timeline.Height);
            Assert.Equal(t1.Id, timeline.SelectedId);

            // 2. Same mutable text list gains second layer
            var t2 = new TimedTextOverlay { Text = "Layer 2", StartMs = 1000, EndMs = 2000 };
            textList.Add(t2);
            timeline.SetLayers(textList, frameList);
            Assert.Equal(94, timeline.Height);
            Assert.Equal(t1.Id, timeline.SelectedId);

            // 3. Selection change
            timeline.SelectLayer(t2.Id);
            Assert.Equal(t2.Id, timeline.SelectedId);

            // 4. Same mutable text list removes selected layer (t2) -> selection falls back to t1
            textList.Remove(t2);
            timeline.SetLayers(textList, frameList);
            Assert.Equal(62, timeline.Height);
            Assert.Equal(t1.Id, timeline.SelectedId);

            // 5. Same mutable text list removes remaining layer -> selection becomes null
            textList.Clear();
            timeline.SetLayers(textList, frameList);
            Assert.Equal(56, timeline.Height);
            Assert.Null(timeline.SelectedId);

            // 6. Same mutable frame list gains layers
            var f1 = new FrameEditLayer { Name = "Frame 1", StartMs = 0, EndMs = 1000 };
            frameList.Add(f1);
            timeline.SetLayers(textList, frameList);
            Assert.Equal(62, timeline.Height);
            Assert.Equal(f1.Id, timeline.SelectedId);

            var f2 = new FrameEditLayer { Name = "Frame 2", StartMs = 1000, EndMs = 2000 };
            frameList.Add(f2);
            timeline.SetLayers(textList, frameList);
            Assert.Equal(94, timeline.Height);

            timeline.SelectLayer(f2.Id);
            frameList.Remove(f2);
            timeline.SetLayers(textList, frameList);
            Assert.Equal(62, timeline.Height);
            Assert.Equal(f1.Id, timeline.SelectedId);

            frameList.Clear();
            timeline.SetLayers(textList, frameList);
            Assert.Equal(56, timeline.Height);
            Assert.Null(timeline.SelectedId);
        });

    [Fact]
    public void VideoLayerTimeline_MeasureOverride_ComputesCorrectGeometryForLayerCombinations() =>
        StaTestHost.Run(() =>
        {
            var timeline = new VideoLayerTimeline();
            var textList = new List<TimedTextOverlay>();
            var frameList = new List<FrameEditLayer>();

            // 1 text + 1 frame
            textList.Add(new TimedTextOverlay { Text = "T1", StartMs = 0, EndMs = 1000 });
            frameList.Add(new FrameEditLayer { Name = "F1", StartMs = 0, EndMs = 1000 });
            timeline.SetLayers(textList, frameList);
            Assert.Equal(68, timeline.Height);
            timeline.Measure(new Size(800, double.PositiveInfinity));
            Assert.Equal(68, timeline.DesiredSize.Height);

            // 2 text + 2 frame
            textList.Add(new TimedTextOverlay { Text = "T2", StartMs = 1000, EndMs = 2000 });
            frameList.Add(new FrameEditLayer { Name = "F2", StartMs = 1000, EndMs = 2000 });
            timeline.SetLayers(textList, frameList);
            Assert.Equal(132, timeline.Height);
            timeline.Measure(new Size(800, double.PositiveInfinity));
            Assert.Equal(132, timeline.DesiredSize.Height);
        });

}
