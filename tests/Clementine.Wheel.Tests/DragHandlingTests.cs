using System.IO;
using System.Reflection;
using System.Windows;
using Clementine.Core;
using Clementine.Core.Hosting;
using Clementine.TestSupport;

namespace Clementine.Wheel.Tests;

/// <summary>
/// Drives the drag-mode wheel window's real DragOver/Drop handlers with genuine <see cref="DragEventArgs"/>
/// (created through WPF's internal constructor), without showing a window or touching the mouse.
/// </summary>
public class DragHandlingTests
{
    static readonly ConstructorInfo DragArgsConstructor = typeof(DragEventArgs).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic,
        [typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point)])
        ?? throw new InvalidOperationException("WPF's DragEventArgs constructor changed.");

    sealed class Rig : IDisposable
    {
        readonly TestWorkspace _workspace = new("drag");

        public Rig()
        {
            Files = Fakes.Files(_workspace, "clip.mp4");
            Catalog = Fakes.Catalog(
                Fakes.Op(Fakes.Info("video.mp4.h264", "MP4", "H.264", order: 10)),
                Fakes.Op(Fakes.Info("video.mp4.h265", "MP4", "H.265", order: 11)),
                Fakes.Op(Fakes.Info("video.dxv", "DXV", order: 100), availability: Availability.Unavailable("Requires FFmpeg")),
                Fakes.Op(Fakes.Info("video.trim", "Trim", order: 10, category: OperationCategory.Tool)));
            Window = new WheelWindow(WheelInteraction.Drag, new RecordingLog())
            {
                MatchMode = held => ModifierBindings.Default.Match(held),
            };
            Window.ModeRequested += mode =>
            {
                Modes.Add(mode);
                Window.Replace(new WheelSession(WheelModel.Build(Catalog, Files, mode)));
            };
            Window.Chosen += (option, _) => Chosen.Add(option.Info.Id);
            Window.View.Show(new WheelSession(WheelModel.Build(Catalog, Files, WheelMode.Convert)), WheelInteraction.Drag);
            Layout();
        }

        public IReadOnlyList<InputFile> Files { get; }

        public OperationCatalog Catalog { get; }

        public WheelWindow Window { get; }

        public List<WheelMode> Modes { get; } = [];

        public List<string> Chosen { get; } = [];

        public WheelSession Session => Window.Session!;

        public void Layout()
        {
            Window.View.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Window.View.Arrange(new Rect(Window.View.DesiredSize));
        }

        public Point At(Vector offset) => Window.View.Centre + offset;

        public DragEventArgs Raise(RoutedEvent routedEvent, Point position,
            DragDropKeyStates keys = DragDropKeyStates.LeftMouseButton | DragDropKeyStates.ShiftKey,
            DragDropEffects allowed = DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link,
            IDataObject? data = null)
        {
            data ??= new DataObject(DataFormats.FileDrop, Files.Select(f => f.Path).ToArray());
            var args = (DragEventArgs)DragArgsConstructor.Invoke([data, keys, allowed, Window.View, position]);
            args.RoutedEvent = routedEvent;
            Window.RaiseEvent(args);
            return args;
        }

        public void Dispose()
        {
            Window.Close();
            _workspace.Dispose();
        }
    }

    [Fact]
    public void Hovering_a_segment_reports_copy_and_the_centre_reports_none()
    {
        Sta.Run(() =>
        {
            using var rig = new Rig();
            var mp4 = rig.Session.Layout.SegmentCentroid(0);
            var over = rig.Raise(DragDrop.DragOverEvent, rig.At(mp4));
            Assert.Equal(DragDropEffects.Copy, over.Effects);
            Assert.Equal(WheelHit.OnSegment(0), rig.Session.Hover);
            Assert.Equal(0, rig.Session.ExpandedSegment);

            var centre = rig.Raise(DragDrop.DragOverEvent, rig.At(new Vector(3, 4)));
            Assert.Equal(DragDropEffects.None, centre.Effects);
            Assert.Equal(WheelHit.Centre, rig.Session.Hover);
        });
    }

    [Fact]
    public void Never_reports_move_even_if_the_source_only_allows_move()
    {
        Sta.Run(() =>
        {
            using var rig = new Rig();
            var args = rig.Raise(DragDrop.DragOverEvent, rig.At(rig.Session.Layout.SegmentCentroid(0)), allowed: DragDropEffects.Move);
            Assert.Equal(DragDropEffects.None, args.Effects);
            var drop = rig.Raise(DragDrop.DropEvent, rig.At(rig.Session.Layout.SegmentCentroid(0)), allowed: DragDropEffects.Move);
            Assert.Equal(DragDropEffects.None, drop.Effects);
            Assert.Empty(rig.Chosen);
        });
    }

    [Fact]
    public void Dropping_on_a_variant_launches_that_variant()
    {
        Sta.Run(() =>
        {
            using var rig = new Rig();
            rig.Raise(DragDrop.DragOverEvent, rig.At(rig.Session.Layout.SegmentCentroid(0)));
            var variant = rig.At(rig.Session.Layout.VariantCentroid(0, 1));
            Assert.Equal(DragDropEffects.Copy, rig.Raise(DragDrop.DragOverEvent, variant).Effects);
            var drop = rig.Raise(DragDrop.DropEvent, variant);
            Assert.Equal(DragDropEffects.Copy, drop.Effects);
            Assert.Equal(["video.mp4.h265"], rig.Chosen);
        });
    }

    [Fact]
    public void Dropping_on_the_inner_segment_launches_the_default()
    {
        Sta.Run(() =>
        {
            using var rig = new Rig();
            var drop = rig.Raise(DragDrop.DropEvent, rig.At(rig.Session.Layout.SegmentCentroid(0)));
            Assert.Equal(DragDropEffects.Copy, drop.Effects);
            Assert.Equal(["video.mp4.h264"], rig.Chosen);
        });
    }

    [Fact]
    public void Unavailable_segments_and_the_centre_refuse_the_drop()
    {
        Sta.Run(() =>
        {
            using var rig = new Rig();
            var dxv = rig.At(rig.Session.Layout.SegmentCentroid(1));
            Assert.Equal(DragDropEffects.None, rig.Raise(DragDrop.DragOverEvent, dxv).Effects);
            Assert.Equal("video.dxv", rig.Session.HoveredOption!.Info.Id);
            Assert.Equal(DragDropEffects.None, rig.Raise(DragDrop.DropEvent, dxv).Effects);
            Assert.Equal(DragDropEffects.None, rig.Raise(DragDrop.DropEvent, rig.At(new Vector())).Effects);
            Assert.Empty(rig.Chosen);
        });
    }

    [Fact]
    public void Holding_ctrl_switches_to_the_tools_wheel_live()
    {
        Sta.Run(() =>
        {
            using var rig = new Rig();
            rig.Raise(DragDrop.DragOverEvent, rig.At(new Vector(0, 0)),
                keys: DragDropKeyStates.LeftMouseButton | DragDropKeyStates.ShiftKey | DragDropKeyStates.ControlKey);
            Assert.Equal([WheelMode.Tools], rig.Modes);
            Assert.Equal(WheelMode.Tools, rig.Session.Model.Mode);
            rig.Layout();
            var trim = rig.At(rig.Session.Layout.SegmentCentroid(0));
            rig.Raise(DragDrop.DropEvent, trim, keys: DragDropKeyStates.ShiftKey | DragDropKeyStates.ControlKey);
            Assert.Equal(["video.trim"], rig.Chosen);

            // Releasing every modifier keeps the current wheel instead of closing it under the pointer.
            using var other = new Rig();
            other.Raise(DragDrop.DragOverEvent, other.At(new Vector(0, 0)), keys: DragDropKeyStates.LeftMouseButton);
            Assert.Empty(other.Modes);
        });
    }

    [Fact]
    public void Drags_without_files_are_ignored()
    {
        Sta.Run(() =>
        {
            using var rig = new Rig();
            var text = new DataObject(DataFormats.UnicodeText, "just some text");
            var args = rig.Raise(DragDrop.DragOverEvent, rig.At(rig.Session.Layout.SegmentCentroid(0)), data: text);
            Assert.Equal(DragDropEffects.None, args.Effects);
            Assert.Equal(WheelHit.None, rig.Session.Hover);
        });
    }
}
