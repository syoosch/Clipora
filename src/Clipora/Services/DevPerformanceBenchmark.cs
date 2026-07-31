#if DEBUG
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clipora.Abstractions;
using Clipora.Interop;
using Clipora.Models;
using Clipora.ViewModels;
using Clipora.Views;

namespace Clipora.Services;

/// <summary>
/// Debug-only history scrolling baseline. It exercises the current production interaction and
/// scrolling implementation against synthetic data without reading development or production data.
/// </summary>
internal static class DevPerformanceBenchmark
{
    private enum BenchmarkProfile
    {
        Text,
        Mixed,
    }

    internal sealed class Result
    {
        public bool Succeeded { get; set; }
        public string? ErrorCode { get; set; }
        public string Version { get; set; } = string.Empty;
        public string Theme { get; set; } = string.Empty;
        public string Profile { get; set; } = string.Empty;
        public string Backdrop { get; set; } = string.Empty;
        public int LogicalProcessorCount { get; set; }
        public int ItemCount { get; set; }
        public int WheelCountPerDirection { get; set; }
        public int WheelIntervalMs { get; set; }
        public long WindowHandle { get; set; }
        public bool ForegroundAtStart { get; set; }
        public bool ForegroundAtEnd { get; set; }
        public bool WindowActiveAtStart { get; set; }
        public bool WindowActiveAtEnd { get; set; }
        public double ElapsedMs { get; set; }
        public double CpuMs { get; set; }
        public double CpuMsPer1000Px { get; set; }
        public double NormalizedCpuPercent { get; set; }
        public double SingleCoreCpuPercent { get; set; }
        public double FrameP50Ms { get; set; }
        public double FrameP95Ms { get; set; }
        public double FrameP99Ms { get; set; }
        public int FramesOver16_7Ms { get; set; }
        public int FramesOver33Ms { get; set; }
        public long CompositionRenderingCallbackCount { get; set; }
        public long CompositionUniqueRenderingTimeCount { get; set; }
        public long CompositionDuplicateRenderingTimeCount { get; set; }
        public long CompositionNonRenderingCallbackCount { get; set; }
        public int DispatcherHeartbeatCount { get; set; }
        public double DispatcherHeartbeatP95Ms { get; set; }
        public double DispatcherHeartbeatP99Ms { get; set; }
        public double DispatcherHeartbeatMaxMs { get; set; }
        public int DispatcherHeartbeatOver50Ms { get; set; }
        public int DispatcherHeartbeatOver100Ms { get; set; }
        public int ScrollChangedEventCount { get; set; }
        public int ScrollChangedCount { get; set; }
        public int ScrollChangedOffsetOnlyCount { get; set; }
        public int ScrollChangedExtentOnlyCount { get; set; }
        public int ScrollChangedViewportOnlyCount { get; set; }
        public int ScrollChangedMixedCount { get; set; }
        public int ScrollChangedOtherCount { get; set; }
        public double AbsoluteScrollDistance { get; set; }
        public double FinalOffset { get; set; }
        public double StopTailMs { get; set; }
        public int ThumbnailLoads { get; set; }
        public long AllocatedBytes { get; set; }
        public int Gen0Collections { get; set; }
        public int Gen1Collections { get; set; }
        public int Gen2Collections { get; set; }
        public long WorkingSetBeforeBytes { get; set; }
        public long WorkingSetAfterBytes { get; set; }
        public long PrivateBytesBefore { get; set; }
        public long PrivateBytesAfter { get; set; }
    }

    public static int Run(string[] args)
    {
        string? outputPath = GetArgument(args, "output");
        if (string.IsNullOrWhiteSpace(outputPath))
            return 2;

        outputPath = Path.GetFullPath(outputPath);
        string readyMarkerPath = outputPath + ".ready";
        string measuringMarkerPath = outputPath + ".measuring";
        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            "clipora-performance-baseline-" + Guid.NewGuid().ToString("N"));
        HistoryWindow? window = null;

        try
        {
            if (!Enum.TryParse(GetArgument(args, "profile"), true, out BenchmarkProfile profile))
                profile = BenchmarkProfile.Text;

            string themeName = string.Equals(
                GetArgument(args, "theme"),
                "LiquidGlass",
                StringComparison.OrdinalIgnoreCase)
                    ? "LiquidGlass"
                    : "Fluent";
            int wheelCount = ParsePositiveInt(GetArgument(args, "wheel-count"), 60);
            int wheelIntervalMs = ParsePositiveInt(GetArgument(args, "wheel-interval-ms"), 100);
            int itemCount = ParsePositiveInt(GetArgument(args, "item-count"), 1000);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var paths = new AppPaths(tempRoot);
            SeedData(paths, profile, itemCount);

            var database = new Database(paths);
            var clips = new SqliteClipStore(database);
            var tags = new SqliteTagStore(database);
            var settings = new SettingsService(paths);
            settings.Current.VisualTheme = themeName;
            settings.Current.ColorMode = "Light";
            settings.Current.ShowBackToTop = true;

            using var monitor = new BenchmarkMonitor();
            using var previewLoader = new ImagePreviewLoader();
            var viewModel = new MainViewModel(clips, tags, monitor, new ClipWriter());
            window = new HistoryWindow(settings, previewLoader)
            {
                DataContext = viewModel,
                AllowClose = true,
                ShowInTaskbar = false,
                ShowActivated = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Width = 380,
                Height = 560,
            };

            var theme = new ThemeService();
            theme.ApplyLiquidGlassTransparency(settings.Current.LiquidGlassTransparency);
            theme.ApplyColorMode(settings.Current.ColorMode);
            theme.ApplyVisualTheme(themeName);
            theme.AttachWindow(window);
            window.WindowBackdropType = theme.CurrentBackdrop;

            window.Show();
            window.Activate();
            window.Topmost = true;
            window.Topmost = settings.Current.AlwaysOnTop;
            window.UpdateLayout();
            window.ItemsList.UpdateLayout();
            PumpDispatcher(TimeSpan.FromSeconds(2));

            ScrollViewer scrollViewer = FindVisualDescendant<ScrollViewer>(window.ItemsList)
                ?? throw new InvalidOperationException("HISTORY_SCROLLVIEWER_MISSING");
            IntPtr windowHandle = new WindowInteropHelper(window).Handle;

            int scrollChangedEventCount = 0;
            int scrollChangedCount = 0;
            int scrollChangedOffsetOnlyCount = 0;
            int scrollChangedExtentOnlyCount = 0;
            int scrollChangedViewportOnlyCount = 0;
            int scrollChangedMixedCount = 0;
            int scrollChangedOtherCount = 0;
            double absoluteScrollDistance = 0;
            long lastOffsetChangeTimestamp = 0;
            bool measurementActive = false;
            ScrollChangedEventHandler scrollChanged = (_, e) =>
            {
                if (!measurementActive)
                    return;

                scrollChangedEventCount++;
                bool hasOffsetChange = Math.Abs(e.VerticalChange) > double.Epsilon;
                bool hasExtentChange = Math.Abs(e.ExtentHeightChange) > double.Epsilon
                    || Math.Abs(e.ExtentWidthChange) > double.Epsilon;
                bool hasViewportChange = Math.Abs(e.ViewportHeightChange) > double.Epsilon
                    || Math.Abs(e.ViewportWidthChange) > double.Epsilon;
                int changeKinds = (hasOffsetChange ? 1 : 0)
                    + (hasExtentChange ? 1 : 0)
                    + (hasViewportChange ? 1 : 0);

                if (changeKinds > 1)
                    scrollChangedMixedCount++;
                else if (hasOffsetChange)
                    scrollChangedOffsetOnlyCount++;
                else if (hasExtentChange)
                    scrollChangedExtentOnlyCount++;
                else if (hasViewportChange)
                    scrollChangedViewportOnlyCount++;
                else
                    scrollChangedOtherCount++;

                if (hasOffsetChange)
                {
                    scrollChangedCount++;
                    absoluteScrollDistance += Math.Abs(e.VerticalChange);
                    lastOffsetChangeTimestamp = Stopwatch.GetTimestamp();
                }
            };
            scrollViewer.ScrollChanged += scrollChanged;

            int thumbnailLoads = 0;
            PropertyChangedEventHandler thumbnailChanged = (_, e) =>
            {
                if (e.PropertyName == nameof(ClipItemViewModel.Thumbnail))
                    thumbnailLoads++;
            };
            foreach (ClipItemViewModel item in viewModel.Items)
                item.PropertyChanged += thumbnailChanged;

            File.WriteAllText(readyMarkerPath, windowHandle.ToInt64().ToString());
            PumpDispatcher(TimeSpan.FromMilliseconds(1500));
            window.Activate();
            NativeMethods.SetForegroundWindow(windowHandle);
            window.ItemsList.Focus();
            Keyboard.Focus(window.ItemsList);
            PumpDispatcher(TimeSpan.FromMilliseconds(250));
            bool foregroundAtStart = NativeMethods.GetForegroundWindow() == windowHandle;
            bool windowActiveAtStart = window.IsActive;

            var frameIntervals = new List<double>(2048);
            TimeSpan? lastRenderingTime = null;
            long compositionRenderingCallbackCount = 0;
            long compositionUniqueRenderingTimeCount = 0;
            long compositionDuplicateRenderingTimeCount = 0;
            long compositionNonRenderingCallbackCount = 0;
            EventHandler rendering = (_, eventArgs) =>
            {
                compositionRenderingCallbackCount++;
                if (eventArgs is not RenderingEventArgs renderingArgs)
                {
                    compositionNonRenderingCallbackCount++;
                    return;
                }

                TimeSpan renderingTime = renderingArgs.RenderingTime;
                if (lastRenderingTime == renderingTime)
                {
                    compositionDuplicateRenderingTimeCount++;
                }
                else
                {
                    compositionUniqueRenderingTimeCount++;
                    if (lastRenderingTime is { } previous && renderingTime > previous)
                        frameIntervals.Add((renderingTime - previous).TotalMilliseconds);
                }
                lastRenderingTime = renderingTime;
            };

            var dispatcherGaps = new List<double>(512);
            long lastHeartbeatTimestamp = Stopwatch.GetTimestamp();
            var heartbeat = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(16),
            };
            heartbeat.Tick += (_, _) =>
            {
                long now = Stopwatch.GetTimestamp();
                dispatcherGaps.Add(
                    (now - lastHeartbeatTimestamp) * 1000d / Stopwatch.Frequency);
                lastHeartbeatTimestamp = now;
            };

            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            TimeSpan cpuBefore = process.TotalProcessorTime;
            long allocationBefore = GC.GetTotalAllocatedBytes(precise: false);
            int gen0Before = GC.CollectionCount(0);
            int gen1Before = GC.CollectionCount(1);
            int gen2Before = GC.CollectionCount(2);
            long workingSetBefore = process.WorkingSet64;
            long privateBytesBefore = process.PrivateMemorySize64;
            var stopwatch = Stopwatch.StartNew();
            long lastWheelInputTimestamp = 0;

            File.WriteAllText(measuringMarkerPath, process.Id.ToString());
            measurementActive = true;
            CompositionTarget.Rendering += rendering;
            heartbeat.Start();
            RunWheelSequence(
                window.ItemsList,
                wheelCount,
                wheelIntervalMs,
                () => lastWheelInputTimestamp = Stopwatch.GetTimestamp());
            PumpDispatcher(TimeSpan.FromMilliseconds(500));
            heartbeat.Stop();
            CompositionTarget.Rendering -= rendering;
            measurementActive = false;
            stopwatch.Stop();
            try { File.Delete(measuringMarkerPath); } catch { }

            process.Refresh();
            double cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            double elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
            double stopTailMs = lastWheelInputTimestamp > 0
                && lastOffsetChangeTimestamp > lastWheelInputTimestamp
                    ? (lastOffsetChangeTimestamp - lastWheelInputTimestamp) * 1000d / Stopwatch.Frequency
                    : 0;
            frameIntervals.Sort();
            dispatcherGaps.Sort();

            var result = new Result
            {
                Succeeded = true,
                Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? string.Empty,
                Theme = themeName,
                Profile = profile.ToString(),
                Backdrop = window.WindowBackdropType.ToString(),
                LogicalProcessorCount = Environment.ProcessorCount,
                ItemCount = viewModel.Items.Count,
                WheelCountPerDirection = wheelCount,
                WheelIntervalMs = wheelIntervalMs,
                WindowHandle = windowHandle.ToInt64(),
                ForegroundAtStart = foregroundAtStart,
                ForegroundAtEnd = NativeMethods.GetForegroundWindow() == windowHandle,
                WindowActiveAtStart = windowActiveAtStart,
                WindowActiveAtEnd = window.IsActive,
                ElapsedMs = elapsedMs,
                CpuMs = cpuMs,
                CpuMsPer1000Px = absoluteScrollDistance > 0
                    ? cpuMs * 1000d / absoluteScrollDistance
                    : 0,
                NormalizedCpuPercent = cpuMs / elapsedMs / Environment.ProcessorCount * 100d,
                SingleCoreCpuPercent = cpuMs / elapsedMs * 100d,
                FrameP50Ms = Percentile(frameIntervals, 0.50),
                FrameP95Ms = Percentile(frameIntervals, 0.95),
                FrameP99Ms = Percentile(frameIntervals, 0.99),
                FramesOver16_7Ms = frameIntervals.Count(value => value > 16.7),
                FramesOver33Ms = frameIntervals.Count(value => value > 33),
                CompositionRenderingCallbackCount = compositionRenderingCallbackCount,
                CompositionUniqueRenderingTimeCount = compositionUniqueRenderingTimeCount,
                CompositionDuplicateRenderingTimeCount = compositionDuplicateRenderingTimeCount,
                CompositionNonRenderingCallbackCount = compositionNonRenderingCallbackCount,
                DispatcherHeartbeatCount = dispatcherGaps.Count,
                DispatcherHeartbeatP95Ms = Percentile(dispatcherGaps, 0.95),
                DispatcherHeartbeatP99Ms = Percentile(dispatcherGaps, 0.99),
                DispatcherHeartbeatMaxMs = dispatcherGaps.Count > 0 ? dispatcherGaps[^1] : 0,
                DispatcherHeartbeatOver50Ms = dispatcherGaps.Count(value => value > 50),
                DispatcherHeartbeatOver100Ms = dispatcherGaps.Count(value => value > 100),
                ScrollChangedEventCount = scrollChangedEventCount,
                ScrollChangedCount = scrollChangedCount,
                ScrollChangedOffsetOnlyCount = scrollChangedOffsetOnlyCount,
                ScrollChangedExtentOnlyCount = scrollChangedExtentOnlyCount,
                ScrollChangedViewportOnlyCount = scrollChangedViewportOnlyCount,
                ScrollChangedMixedCount = scrollChangedMixedCount,
                ScrollChangedOtherCount = scrollChangedOtherCount,
                AbsoluteScrollDistance = absoluteScrollDistance,
                FinalOffset = scrollViewer.VerticalOffset,
                StopTailMs = stopTailMs,
                ThumbnailLoads = thumbnailLoads,
                AllocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - allocationBefore,
                Gen0Collections = GC.CollectionCount(0) - gen0Before,
                Gen1Collections = GC.CollectionCount(1) - gen1Before,
                Gen2Collections = GC.CollectionCount(2) - gen2Before,
                WorkingSetBeforeBytes = workingSetBefore,
                WorkingSetAfterBytes = process.WorkingSet64,
                PrivateBytesBefore = privateBytesBefore,
                PrivateBytesAfter = process.PrivateMemorySize64,
            };

            WriteResult(outputPath, result);

            scrollViewer.ScrollChanged -= scrollChanged;
            foreach (ClipItemViewModel item in viewModel.Items)
                item.PropertyChanged -= thumbnailChanged;
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                WriteResult(outputPath, new Result
                {
                    Succeeded = false,
                    ErrorCode = ex.GetType().Name + ": " + ex.Message,
                });
            }
            catch
            {
                // Preserve the original benchmark failure.
            }
            return 1;
        }
        finally
        {
            try { File.Delete(readyMarkerPath); } catch { }
            try { File.Delete(measuringMarkerPath); } catch { }
            if (window is not null)
            {
                try { window.Close(); } catch { }
            }
            try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static void SeedData(AppPaths paths, BenchmarkProfile profile, int itemCount)
    {
        string baseThumbnail = Path.Combine(paths.ThumbsDir, "base.png");
        if (profile == BenchmarkProfile.Mixed)
            WriteBaseThumbnail(baseThumbnail);

        var store = new SqliteClipStore(new Database(paths));
        DateTime createdAt = DateTime.UtcNow;
        int imageIndex = 0;

        for (int index = 0; index < itemCount; index++)
        {
            ClipType type = profile == BenchmarkProfile.Text
                ? ClipType.Text
                : (index % 10) switch
                {
                    0 or 1 => ClipType.Image,
                    2 => ClipType.Url,
                    3 => ClipType.Code,
                    _ => ClipType.Text,
                };

            string preview = type switch
            {
                ClipType.Image => $"synthetic image {index}",
                ClipType.Url => $"https://example.invalid/item/{index}",
                ClipType.Code => $"public static int Item{index}() => {index};",
                _ => $"Synthetic clipboard text item {index} with a stable preview line.",
            };
            string? thumbnailPath = null;
            if (type == ClipType.Image)
            {
                thumbnailPath = Path.Combine(paths.ThumbsDir, $"thumb-{imageIndex++:D4}.png");
                File.Copy(baseThumbnail, thumbnailPath);
            }

            store.Add(new ClipItem
            {
                Type = type,
                PreviewText = preview,
                TextContent = type == ClipType.Image ? null : preview,
                RefPath = thumbnailPath,
                ThumbnailPath = thumbnailPath,
                SourceApp = index % 2 == 0 ? "SyntheticApp" : "BenchmarkHost",
                CreatedAt = createdAt.AddMilliseconds(-index),
                ContentHash = "performance-baseline-" + index,
                SizeBytes = preview.Length * 2L,
            }, mergeDuplicates: false);
        }
    }

    private static void WriteBaseThumbnail(string path)
    {
        const int width = 240;
        const int height = 160;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                pixels[offset] = (byte)(80 + x * 120 / width);
                pixels[offset + 1] = (byte)(90 + y * 100 / height);
                pixels[offset + 2] = 150;
                pixels[offset + 3] = 255;
            }
        }

        BitmapSource bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        ClipboardImageNormalizer.SavePng(bitmap, path);
    }

    private static void RunWheelSequence(
        ListBox listBox,
        int wheelCount,
        int intervalMs,
        Action recordWheelInput)
    {
        var frame = new DispatcherFrame();
        int downCount = 0;
        int upCount = 0;
        bool pauseBeforeUp = false;
        var timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(intervalMs),
        };
        timer.Tick += (_, _) =>
        {
            if (downCount < wheelCount)
            {
                recordWheelInput();
                RaiseWheel(listBox, -120);
                downCount++;
                if (downCount == wheelCount)
                {
                    pauseBeforeUp = true;
                    timer.Interval = TimeSpan.FromMilliseconds(250);
                }
                return;
            }

            if (pauseBeforeUp)
            {
                pauseBeforeUp = false;
                timer.Interval = TimeSpan.FromMilliseconds(intervalMs);
                return;
            }

            if (upCount < wheelCount)
            {
                recordWheelInput();
                RaiseWheel(listBox, 120);
                upCount++;
                return;
            }

            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void RaiseWheel(ListBox listBox, int delta)
    {
        UIElement source = FindVisualDescendant<ListBoxItem>(listBox) is { } item ? item : listBox;
        int timestamp = Environment.TickCount;
        var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, timestamp, delta)
        {
            RoutedEvent = Mouse.PreviewMouseWheelEvent,
            Source = source,
        };
        source.RaiseEvent(preview);
        if (preview.Handled)
            return;

        var bubble = new MouseWheelEventArgs(Mouse.PrimaryDevice, timestamp, delta)
        {
            RoutedEvent = Mouse.MouseWheelEvent,
            Source = source,
        };
        source.RaiseEvent(bubble);
    }

    private static T? FindVisualDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                return match;
            T? nested = FindVisualDescendant<T>(child);
            if (nested is not null)
                return nested;
        }
        return null;
    }

    private static void PumpDispatcher(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = duration,
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
            return 0;
        int index = Math.Clamp(
            (int)Math.Ceiling(sortedValues.Count * percentile) - 1,
            0,
            sortedValues.Count - 1);
        return sortedValues[index];
    }

    private static string? GetArgument(string[] args, string name)
    {
        string prefix = "--" + name + "=";
        string? inline = args.FirstOrDefault(
            value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (inline is not null)
            return inline[prefix.Length..].Trim('"');

        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], "--" + name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1].Trim('"');
        }
        return null;
    }

    private static int ParsePositiveInt(string? value, int fallback) =>
        int.TryParse(value, out int parsed) && parsed > 0 ? parsed : fallback;

    private static void WriteResult(string path, Result result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class BenchmarkMonitor : IClipboardMonitor
    {
        public event EventHandler<ClipItem>? ClipCaptured { add { } remove { } }
        public event EventHandler<long>? ItemOverSized { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public bool Import(IDataObject dataObject) => false;
        public void Dispose() { }
    }
}
#endif
