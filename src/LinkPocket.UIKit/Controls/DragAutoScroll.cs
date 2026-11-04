using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace LinkPocket.Views;

/// <summary>
/// 拖拽悬停时的**纵向自动滚动**（资源管理器口径）：指针拖到可滚区域的上下边缘时，
/// 该区域按距边缘的远近连续滚动；指针移出区域 / 松手即停。
///
/// <para>为什么不靠 OLE 的 DragOver 逐次触发：指针停在边缘不动时不再有新事件，
/// 但用户期望"按住就继续滚"。因此 DragOver 只负责给出方向，滚动由 16ms 定时器
/// 按**系统光标**持续驱动（与浮层跟随同一取值来源）。</para>
///
/// <para>只做纵向：横向滚动（面包屑）有自己的拖拽自动滚；树的滚轮也明确不允许横向。</para>
/// </summary>
public sealed class DragAutoScroll
{
    private const double EdgeZone = 40;
    private const double HoldDelayMs = 110;
    private const double MinSpeed = 180;
    private const double MaxSpeed = 900;

    private readonly Func<ScrollViewer?> _scroller;
    private readonly DispatcherTimer _timer;

    private DateTime _eligibleAt;
    private DateTime _lastTick;
    private DateTime _lastItemStep;
    private int _direction;
    private double _speed;

    /// <summary>每真正滚动一次触发（供宿主在滚动后重算指针下的落点）。</summary>
    public event EventHandler? Scrolled;

    private DragAutoScroll(FrameworkElement host, Func<ScrollViewer?> scroller)
    {
        _scroller = scroller;
        _timer = new DispatcherTimer(DispatcherPriority.Render, host.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _timer.Tick += OnTick;
    }

    /// <summary>挂到宿主上（宿主可以是表格/面板本身；内部按 <paramref name="scroller"/> 取滚动宿主）。</summary>
    public static DragAutoScroll Attach(FrameworkElement host, Func<ScrollViewer?> scroller)
    {
        var auto = new DragAutoScroll(host, scroller);
        host.AddHandler(UIElement.DragEnterEvent, new DragEventHandler(auto.OnDragOver), true);
        host.AddHandler(UIElement.DragOverEvent, new DragEventHandler(auto.OnDragOver), true);
        host.AddHandler(UIElement.DropEvent, new DragEventHandler(auto.OnDragEnd), true);
        host.Unloaded += auto.OnHostUnloaded;
        return auto;
    }

    private void OnHostUnloaded(object sender, RoutedEventArgs e) => Stop();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var scroller = _scroller();
        if (scroller == null || scroller.ScrollableHeight <= 0.5)
        {
            Stop();
            return;
        }

        var point = e.GetPosition(scroller);
        var direction = DirectionAt(point.Y, scroller.ActualHeight, out var strength);
        if (direction == 0)
        {
            Stop();
            return;
        }

        if (direction != _direction || !_timer.IsEnabled)
        {
            _eligibleAt = DateTime.UtcNow.AddMilliseconds(HoldDelayMs);
            _lastTick = DateTime.UtcNow;
        }
        _direction = direction;
        _speed = MinSpeed + strength * (MaxSpeed - MinSpeed);
        if (!_timer.IsEnabled) _timer.Start();
    }

    private static int DirectionAt(double y, double height, out double strength)
    {
        strength = 0;
        if (height <= EdgeZone * 2) return 0;   // 区域太矮：不做自动滚（避免整块都算边缘）
        if (y < EdgeZone)
        {
            strength = Math.Clamp((EdgeZone - y) / EdgeZone, 0, 1);
            return -1;
        }
        if (y > height - EdgeZone)
        {
            strength = Math.Clamp((y - (height - EdgeZone)) / EdgeZone, 0, 1);
            return 1;
        }
        return 0;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var scroller = _scroller();
        if (scroller == null || !GetCursorPos(out var screen) || !IsAnyDragButtonDown())
        {
            Stop();
            return;
        }

        Point point;
        try
        {
            point = scroller.PointFromScreen(new Point(screen.X, screen.Y));
        }
        catch (InvalidOperationException)
        {
            Stop();
            return;
        }

        if (point.X < 0 || point.X > scroller.ActualWidth
            || point.Y < 0 || point.Y > scroller.ActualHeight
            || scroller.ScrollableHeight <= 0.5)
        {
            Stop();
            return;
        }

        var now = DateTime.UtcNow;
        var direction = DirectionAt(point.Y, scroller.ActualHeight, out var strength);
        if (direction == 0)
        {
            Stop();
            return;
        }
        _direction = direction;
        _speed = MinSpeed + strength * (MaxSpeed - MinSpeed);
        if (now < _eligibleAt)
        {
            _lastTick = now;
            return;
        }

        var dt = Math.Min(0.05, Math.Max(0.001, (now - _lastTick).TotalSeconds));
        _lastTick = now;
        var before = scroller.VerticalOffset;
        if ((direction < 0 && before <= 0.5)
            || (direction > 0 && before >= scroller.ScrollableHeight - 0.5))
        {
            Stop();
            return;
        }

        if (scroller.CanContentScroll)
        {
            // 按项滚动：每 60ms 走一项（像素速度在“项为单位”的 ScrollViewer 里会变成乱跳）。
            if ((now - _lastItemStep).TotalMilliseconds < 60) return;
            _lastItemStep = now;
            if (direction < 0) scroller.LineUp();
            else scroller.LineDown();
        }
        else
        {
            scroller.ScrollToVerticalOffset(before + direction * _speed * dt);
        }

        Scrolled?.Invoke(this, EventArgs.Empty);
    }

    private void OnDragEnd(object sender, DragEventArgs e) => Stop();

    private void Stop()
    {
        _direction = 0;
        _speed = 0;
        if (_timer.IsEnabled) _timer.Stop();
    }

    private static bool IsAnyDragButtonDown()
        => (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out ScreenPoint point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint
    {
        public int X;
        public int Y;
    }
}
