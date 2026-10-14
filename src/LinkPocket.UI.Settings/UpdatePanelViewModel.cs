using System.ComponentModel;
using System.Runtime.CompilerServices;
using LinkPocket.Contracts;

namespace LinkPocket.ViewModels
{
    /// <summary>更新面板状态（视图把状态映射成文案 / 可见性；VM 不碰控件、不取词）。</summary>
    public enum UpdatePanelState
    {
        /// <summary>尚未检查（进面板时若有后台自动检查的产物会直接跳到对应状态）。</summary>
        Idle,

        /// <summary>正在检查更新源。</summary>
        Checking,

        /// <summary>已是最新（或更新源只有预发布）。</summary>
        UpToDate,

        /// <summary>已发现新版本，正在下载 / 解包（<see cref="UpdatePanelViewModel.Progress"/> 有效）。</summary>
        Downloading,

        /// <summary>新版本已就位，可「重启并更新」。</summary>
        Ready,

        /// <summary>失败（<see cref="UpdatePanelViewModel.ErrorMessage"/> 带原因）。</summary>
        Failed,
    }

    /// <summary>
    /// 更新面板视图模型（UPDATE.md §6）：检查 → 下载就位 → 交棒。
    /// 状态与动作在 VM（可单测），文案与可见性在视图（code-behind 映射 Loc，随语言切换自动重算）。
    /// </summary>
    public sealed class UpdatePanelViewModel : INotifyPropertyChanged
    {
        private readonly IUpdateService _service;
        private bool _busy;

        public UpdatePanelViewModel(IUpdateService service) => _service = service;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>当前版本（3 段；语言无关，视图直接拼 "v" 显示）。</summary>
        public string CurrentVersion => _service.CurrentVersion;

        public UpdatePanelState State { get; private set; } = UpdatePanelState.Idle;

        /// <summary>本次检查发现的新版本号。</summary>
        public string? LatestVersion { get; private set; }

        /// <summary>失败原因（State = Failed 时有效）。</summary>
        public string? ErrorMessage { get; private set; }

        /// <summary>下载 + 解包进度（0–100）。</summary>
        public double Progress { get; private set; }

        /// <summary>上次更新结果（进面板时消费一次；null = 没有可展示的）。</summary>
        public UpdateLastResult? LastUpdate { get; private set; }

        /// <summary>启动时自动检查（持久化在更新服务自家文件；切换立即落盘）。</summary>
        public bool AutoCheckOnStartup
        {
            get => _service.AutoCheckOnStartup;
            set
            {
                if (_service.AutoCheckOnStartup == value) return;
                _service.AutoCheckOnStartup = value;
                OnPropertyChanged();
            }
        }

        /// <summary>进面板时调用：消费上次更新结果 + 反映后台自动检查的产物（正在动的状态不被覆盖）。</summary>
        public void Refresh()
        {
            LastUpdate = _service.ConsumeLastResult();
            if (State is UpdatePanelState.Checking or UpdatePanelState.Downloading)
            {
                OnPropertyChanged(string.Empty);
                return;
            }

            if (_service.LastPrepare is { Status: UpdatePrepareStatus.Ready } prepared)
            {
                Set(UpdatePanelState.Ready, prepared.PreparedVersion);
                return;
            }

            var check = _service.LastCheck;
            switch (check?.Status)
            {
                case UpdateCheckStatus.UpToDate:
                    Set(UpdatePanelState.UpToDate, check.LatestVersion);
                    return;
                case UpdateCheckStatus.Failed:
                    Set(UpdatePanelState.Failed, null, check.Error);
                    return;
                case UpdateCheckStatus.Available:
                    // 就位产物丢了（比如应用重启过）→ 回到初始态，由「检查更新」重走（会跳过重复下载吗——不会，重下一次）
                    Set(UpdatePanelState.Idle, check.LatestVersion);
                    return;
                default:
                    OnPropertyChanged(string.Empty);
                    return;
            }
        }

        /// <summary>检查更新；发现新版本则继续下载就位（最近一次已就位同一版本时跳过重复下载）。</summary>
        public async Task CheckAndPrepareAsync()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                Set(UpdatePanelState.Checking, null);
                var check = await _service.CheckAsync().ConfigureAwait(true);
                switch (check.Status)
                {
                    case UpdateCheckStatus.UpToDate:
                        Set(UpdatePanelState.UpToDate, check.LatestVersion);
                        return;
                    case UpdateCheckStatus.Failed:
                        Set(UpdatePanelState.Failed, null, check.Error);
                        return;
                }

                LatestVersion = check.LatestVersion;
                if (_service.LastPrepare is { Status: UpdatePrepareStatus.Ready } ready
                    && string.Equals(ready.PreparedVersion, check.LatestVersion, StringComparison.Ordinal))
                {
                    Set(UpdatePanelState.Ready, check.LatestVersion);
                    return;
                }

                Set(UpdatePanelState.Downloading, check.LatestVersion);
                var prepared = await _service.PrepareAsync(CreateProgress()).ConfigureAwait(true);
                if (prepared.Status == UpdatePrepareStatus.Ready)
                    Set(UpdatePanelState.Ready, prepared.PreparedVersion ?? check.LatestVersion);
                else
                    Set(UpdatePanelState.Failed, check.LatestVersion, prepared.Error);
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>从本地 zip 就位（校验 / 解包与下载路径同一实现）。</summary>
        public async Task PrepareFromZipAsync(string zipPath)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                Set(UpdatePanelState.Downloading, null);
                var prepared = await _service.PrepareFromZipAsync(zipPath, CreateProgress()).ConfigureAwait(true);
                if (prepared.Status == UpdatePrepareStatus.Ready)
                    Set(UpdatePanelState.Ready, prepared.PreparedVersion);
                else
                    Set(UpdatePanelState.Failed, null, prepared.Error);
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>交棒更新器：成功 = null（调用方随后应关闭应用）；失败 = 原因（应留在应用里）。</summary>
        public string? HandOff() => _service.HandOffToUpdater();

        private IProgress<double> CreateProgress()
            => new Progress<double>(value =>
            {
                Progress = value;
                OnPropertyChanged(nameof(Progress));
            });

        private void Set(UpdatePanelState state, string? latestVersion, string? error = null)
        {
            State = state;
            LatestVersion = latestVersion;
            ErrorMessage = error;
            OnPropertyChanged(string.Empty);   // 广播全刷（状态是成套变的，逐属性点报没有收益）
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
