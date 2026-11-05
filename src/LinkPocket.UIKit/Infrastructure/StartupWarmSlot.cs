using System.Threading;

namespace LinkPocket.ViewModels;

/// <summary>
/// 一次启动预热的所有权：取消方与任务收尾方经短锁交接，
/// <see cref="CancellationTokenSource"/> 恰好释放一次，且绝不会在 <see cref="Cancel"/> 进行中被释放。
/// </summary>
public sealed class StartupWarmSlot
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts = new();
    private int _cancelers;
    private bool _finished;

    public CancellationToken Token { get; }

    public StartupWarmSlot()
    {
        Token = _cts.Token;
    }

    public void Cancel()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_finished || _cts is null) return;
            cts = _cts;
            _cancelers++;
        }

        try
        {
            cts.Cancel();
        }
        finally
        {
            ReleaseCanceler();
        }
    }

    public void Finish()
    {
        CancellationTokenSource? dispose = null;
        lock (_gate)
        {
            if (_finished) return;
            _finished = true;
            if (_cancelers == 0)
            {
                dispose = _cts;
                _cts = null;
            }
        }

        dispose?.Dispose();
    }

    private void ReleaseCanceler()
    {
        CancellationTokenSource? dispose = null;
        lock (_gate)
        {
            _cancelers--;
            if (_finished && _cancelers == 0)
            {
                dispose = _cts;
                _cts = null;
            }
        }

        dispose?.Dispose();
    }
}
