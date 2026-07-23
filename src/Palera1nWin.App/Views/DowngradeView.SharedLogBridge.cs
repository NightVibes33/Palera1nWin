using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Palera1nWin.App.ViewModels;

namespace Palera1nWin.App.Views;

public partial class DowngradeView
{
    private static readonly ConditionalWeakTable<DowngradeView, SharedLogBridgeState> SharedLogBridges = new();

    static DowngradeView()
    {
        EventManager.RegisterClassHandler(
            typeof(DowngradeView),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is DowngradeView view) view.EnsureSharedLogBridge();
            }));
    }

    private void EnsureSharedLogBridge()
    {
        if (SharedLogBridges.TryGetValue(this, out _)) return;
        var state = new SharedLogBridgeState(this, _logPath);
        SharedLogBridges.Add(this, state);
        state.Start();
    }

    private sealed class SharedLogBridgeState : IDisposable
    {
        private readonly WeakReference<DowngradeView> _view;
        private readonly string _path;
        private readonly DispatcherTimer _timer;
        private readonly ConcurrentQueue<string> _pending = new();
        private long _offset;
        private bool _disposed;

        public SharedLogBridgeState(DowngradeView view, string path)
        {
            _view = new WeakReference<DowngradeView>(view);
            _path = path;
            _timer = new DispatcherTimer(DispatcherPriority.Background, view.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(350),
            };
            _timer.Tick += (_, _) => Pump();
        }

        public void Start()
        {
            _offset = File.Exists(_path) ? new FileInfo(_path).Length : 0;
            _timer.Start();
        }

        private void Pump()
        {
            if (_disposed || !_view.TryGetTarget(out var view) || view._disposed)
            {
                Dispose();
                return;
            }

            try
            {
                if (File.Exists(_path))
                {
                    using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length < _offset) _offset = 0;
                    if (stream.Length > _offset)
                    {
                        stream.Position = _offset;
                        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                        while (reader.ReadLine() is { } line)
                        {
                            if (!string.IsNullOrWhiteSpace(line)) _pending.Enqueue(line);
                        }
                        _offset = stream.Position;
                    }
                }
            }
            catch (IOException)
            {
                // A native process may be appending at the same moment. Retry next tick.
            }

            var shell = view.DataContext as MainViewModel ??
                        Application.Current?.MainWindow?.DataContext as MainViewModel;
            if (shell is null) return;

            while (_pending.TryDequeue(out var line))
            {
                shell.AppendLog(
                    "darksword",
                    line,
                    line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("exception", StringComparison.OrdinalIgnoreCase));
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
        }
    }
}
