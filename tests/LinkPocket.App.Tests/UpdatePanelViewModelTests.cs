using System.Threading;
using System.Threading.Tasks;
using LinkPocket.Contracts;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>更新面板 VM：状态机的可观测行为（对接假 IUpdateService，不碰网络 / 进程）。</summary>
public class UpdatePanelViewModelTests
{
    private sealed class FakeUpdateService : IUpdateService
    {
        private bool _auto = true;

        public string CurrentVersion { get; init; } = "3.2.4";
        public int AutoCheckWrites { get; private set; }

        public bool AutoCheckOnStartup
        {
            get => _auto;
            set { if (_auto == value) return; _auto = value; AutoCheckWrites++; }
        }

        public UpdateCheckResult? LastCheck { get; set; }
        public UpdatePrepareResult? LastPrepare { get; set; }

        public UpdateCheckResult CheckResult { get; set; } = new(UpdateCheckStatus.UpToDate, "3.2.4");
        public UpdatePrepareResult PrepareResult { get; set; } = new(UpdatePrepareStatus.Ready, "4.0.0");
        public UpdateLastResult? LastResult { get; set; }
        public string? HandOffError { get; set; }

        public int CheckCalls { get; private set; }
        public int PrepareCalls { get; private set; }
        public int PrepareFromZipCalls { get; private set; }
        public int HandOffCalls { get; private set; }
        public int ConsumeCalls { get; private set; }

        public Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
        {
            CheckCalls++;
            LastCheck = CheckResult;
            return Task.FromResult(CheckResult);
        }

        public Task<UpdatePrepareResult> PrepareAsync(IProgress<double>? progress = null, CancellationToken ct = default)
        {
            PrepareCalls++;
            progress?.Report(50);
            LastPrepare = PrepareResult;
            return Task.FromResult(PrepareResult);
        }

        public Task<UpdatePrepareResult> PrepareFromZipAsync(string zipPath, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            PrepareFromZipCalls++;
            LastPrepare = PrepareResult;
            return Task.FromResult(PrepareResult);
        }

        public string? HandOffToUpdater()
        {
            HandOffCalls++;
            return HandOffError;
        }

        public UpdateLastResult? ConsumeLastResult()
        {
            ConsumeCalls++;
            return LastResult;
        }
    }

    private static UpdateCheckResult Available(string version = "4.0.0")
        => new(UpdateCheckStatus.Available, "3.2.4", version, null, $"https://example.invalid/{version}.zip", null, 0);

    [Fact]
    public async Task 检查到新版本_下载就位_进入可重启()
    {
        var service = new FakeUpdateService { CheckResult = Available() };
        var vm = new UpdatePanelViewModel(service);

        await vm.CheckAndPrepareAsync();

        Assert.Equal(UpdatePanelState.Ready, vm.State);
        Assert.Equal("4.0.0", vm.LatestVersion);
        Assert.Equal(1, service.CheckCalls);
        Assert.Equal(1, service.PrepareCalls);
    }

    [Fact]
    public async Task 已是最新_不进下载()
    {
        var service = new FakeUpdateService { CheckResult = new(UpdateCheckStatus.UpToDate, "3.2.4", "3.2.4") };
        var vm = new UpdatePanelViewModel(service);

        await vm.CheckAndPrepareAsync();

        Assert.Equal(UpdatePanelState.UpToDate, vm.State);
        Assert.Equal(0, service.PrepareCalls);
    }

    [Fact]
    public async Task 检查失败_带原因进失败态()
    {
        var service = new FakeUpdateService { CheckResult = new(UpdateCheckStatus.Failed, "3.2.4", Error: "offline") };
        var vm = new UpdatePanelViewModel(service);

        await vm.CheckAndPrepareAsync();

        Assert.Equal(UpdatePanelState.Failed, vm.State);
        Assert.Equal("offline", vm.ErrorMessage);
    }

    [Fact]
    public async Task 下载失败_带原因进失败态()
    {
        var service = new FakeUpdateService
        {
            CheckResult = Available(),
            PrepareResult = new(UpdatePrepareStatus.Failed, Error: "digest mismatch"),
        };
        var vm = new UpdatePanelViewModel(service);

        await vm.CheckAndPrepareAsync();

        Assert.Equal(UpdatePanelState.Failed, vm.State);
        Assert.Equal("digest mismatch", vm.ErrorMessage);
    }

    [Fact]
    public async Task 同版本已就位_跳过一次重复下载()
    {
        var service = new FakeUpdateService
        {
            CheckResult = Available(),
            LastPrepare = new UpdatePrepareResult(UpdatePrepareStatus.Ready, "4.0.0"),
        };
        var vm = new UpdatePanelViewModel(service);

        await vm.CheckAndPrepareAsync();

        Assert.Equal(UpdatePanelState.Ready, vm.State);
        Assert.Equal(0, service.PrepareCalls);
    }

    [Fact]
    public async Task 本地zip_就位进入可重启()
    {
        var service = new FakeUpdateService();
        var vm = new UpdatePanelViewModel(service);

        await vm.PrepareFromZipAsync("C:/fake/LinkPocket_v4.0.0.zip");

        Assert.Equal(UpdatePanelState.Ready, vm.State);
        Assert.Equal("4.0.0", vm.LatestVersion);
        Assert.Equal(1, service.PrepareFromZipCalls);
    }

    [Fact]
    public void 进面板刷新_消费上次更新结果与后台产物()
    {
        var service = new FakeUpdateService
        {
            LastResult = new UpdateLastResult(true, "direct", "3.2.4", "4.0.0", null, "2026-10-15T00:00:00Z"),
            LastPrepare = new UpdatePrepareResult(UpdatePrepareStatus.Ready, "4.0.0"),
        };
        var vm = new UpdatePanelViewModel(service);

        vm.Refresh();

        Assert.Equal(1, service.ConsumeCalls);
        Assert.NotNull(vm.LastUpdate);
        Assert.True(vm.LastUpdate!.Ok);
        Assert.Equal("3.2.4", vm.LastUpdate.FromVersion);
        Assert.Equal(UpdatePanelState.Ready, vm.State);   // 后台自动检查已就位 → 直接可重启
    }

    [Fact]
    public void 进面板刷新_后台检查失败也如实反映()
    {
        var service = new FakeUpdateService
        {
            LastCheck = new UpdateCheckResult(UpdateCheckStatus.Failed, "3.2.4", Error: "unreachable"),
        };
        var vm = new UpdatePanelViewModel(service);

        vm.Refresh();

        Assert.Equal(UpdatePanelState.Failed, vm.State);
        Assert.Equal("unreachable", vm.ErrorMessage);
    }

    [Fact]
    public void 自动检查开关_透传且只在变化时落盘()
    {
        var service = new FakeUpdateService();
        var vm = new UpdatePanelViewModel(service);

        Assert.True(vm.AutoCheckOnStartup);
        vm.AutoCheckOnStartup = false;
        vm.AutoCheckOnStartup = false;   // 同值 = 不重写

        Assert.False(vm.AutoCheckOnStartup);
        Assert.Equal(1, service.AutoCheckWrites);
    }

    [Fact]
    public void 交棒_成功与失败如实透传()
    {
        var service = new FakeUpdateService { HandOffError = null };
        var vm = new UpdatePanelViewModel(service);

        Assert.Null(vm.HandOff());
        Assert.Equal(1, service.HandOffCalls);

        service.HandOffError = "the update is not prepared yet";
        Assert.Equal("the update is not prepared yet", vm.HandOff());
    }
}
