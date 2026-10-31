using System;
using System.Linq;
using System.Threading.Tasks;
using LinkPocket.I18n;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 编辑器横幅的两种语义：**出错**（要用户处理）与**提示**（这一项没弄到，可以自己填）。
/// <para>
/// 判据是"能不能继续干活"：页面本来就没写描述时，抓取只会给一条「未解析到描述」——
/// 那是信息，不是表单错误。把它当成错误来挡保存，用户既没写错也点不了保存，只能靠改一个字段来解锁；
/// 更糟的是保存本身失败一次也会把按钮永久灰住。所以这里钉三件事：
/// ① 有提示时保存照样可用且能真的存下去；② 改字段后提示消失；③ 出错时横幅照样显示（错误优先于提示）。
/// </para>
/// </summary>
public class LinkEditorBannerTests
{
    [Fact]
    public async Task 抓取提示_不挡保存_且能真的存下去()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            vm.NewLinkCommand.Execute(null);
            var editor = Assert.IsType<LinkEditorViewModel>(vm.EditorPage);

            editor.Url = "https://banner.example/no-desc";
            editor.LinkTitle = "没有描述的页面";
            editor.Notice = Loc.K("editor.notFound.description");   // 抓取回来的信息性提示

            Assert.True(editor.HasBanner);
            Assert.False(editor.HasError);                            // 提示不是错误
            Assert.True(editor.SaveCommand.CanExecute(null), "有提示时保存必须仍然可用");

            editor.SaveCommand.Execute(null);

            var saved = await WaitUntilAsync(
                () => !vm.IsEditorPageOpen && vm.Rows.Any(r => r.Name == "没有描述的页面"),
                TimeSpan.FromSeconds(5));
            Assert.True(saved, "有提示时保存应真的落地（页面关闭 + 列表出现新行）");
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 抓取提示_用户一改字段就消失()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            vm.NewLinkCommand.Execute(null);
            var editor = Assert.IsType<LinkEditorViewModel>(vm.EditorPage);

            editor.Notice = Loc.K("editor.notFound.description");
            Assert.True(editor.HasBanner);

            editor.Description = "自己写一段";
            Assert.False(editor.HasBanner);
            Assert.Null(editor.Notice);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 出错与提示并存_横幅显示错误_保存仍可点()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            vm.NewLinkCommand.Execute(null);
            var editor = Assert.IsType<LinkEditorViewModel>(vm.EditorPage);

            editor.Notice = Loc.K("editor.notFound.description");
            editor.Error = Loc.K("editor.urlRequired");

            Assert.True(editor.HasError);
            Assert.True(editor.HasBanner);
            Assert.Equal(editor.Error, editor.Banner);   // 错误优先：更需要被看见
            Assert.True(editor.SaveCommand.CanExecute(null), "出错后也要能再点保存（改对即重试）");
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }
}
