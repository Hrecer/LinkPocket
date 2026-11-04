using LinkPocket.I18n;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

public class SmartListResultViewModelTests
{
    [Fact]
    public async Task Load_Default_Title_Ascending_And_Sort_Reorders_In_Memory()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await using (var db = new LinkPocket.Data.LinkPocketDbContext(dbPath))
            {
                db.Folders.AddRange(
                    new LinkPocket.Data.Folder { FolderId = "000000000001", Name = "B" },
                    new LinkPocket.Data.Folder { FolderId = "000000000002", Name = "A" });
                db.Links.AddRange(
                    new LinkPocket.Data.Link
                    {
                        LinkId = "SLSORT0000000001", Url = "https://c.example", Title = "C", VisitCount = 3,
                        ListId = "000000000001",
                    },
                    new LinkPocket.Data.Link
                    {
                        LinkId = "SLSORT0000000002", Url = "https://b.example", Title = "B", VisitCount = 1,
                        ListId = "000000000002",
                    },
                    new LinkPocket.Data.Link
                    {
                        LinkId = "SLSORT0000000003", Url = "https://a.example", Title = "A", VisitCount = 2,
                    });
                await db.SaveChangesAsync();
            }

            // 「位置」排序 = 目录树前序整数键（根级 0，其后与树顺序一致），不逐目录拼显示路径。
            var pathOrder = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["000000000001"] = 1,
                ["000000000002"] = 2,
            };
            var vm = new SmartListResultViewModel(client, "recently_added",
                Loc.K("smartlists.preset.added"), null, null, _ => LocValue.Empty,
                resolveFolderOrder: folderId => folderId == null
                    ? 0
                    : pathOrder.TryGetValue(folderId, out var order) ? order : int.MaxValue);
            await vm.LoadAsync();

            // 默认 = 名称升序（与页面默认排序一致）
            Assert.Equal(["A", "B", "C"], vm.Items.Select(i => i.Title));

            // 点「查看次数」表头 → VM 内存重排（模板模式控件不内部排序）
            Assert.True(vm.Sort("visit_count", true));
            Assert.Equal(["B", "A", "C"], vm.Items.Select(i => i.Title));
            Assert.False(vm.Sort("visit_count", true));   // 排序未变化：视图无需重绑

            // 排序不丢选中：选中行随数据项走，IsSelected 重新投影
            var selectedId = vm.Items[1].LinkId;
            vm.Selection.SelectSingle(selectedId);
            Assert.True(vm.Items[1].IsSelected);

            Assert.True(vm.Sort("title", false));
            Assert.Equal(["C", "B", "A"], vm.Items.Select(i => i.Title));
            Assert.True(vm.Selection.Contains(selectedId));
            Assert.True(vm.Items.Single(i => i.LinkId == selectedId).IsSelected);

            // 位置：根级最前，其后按目录树前序（F-B=1 在 F-A=2 之前）——与目录树显示顺序一致。
            Assert.True(vm.Sort("path", true));
            Assert.Equal(["A", "C", "B"], vm.Items.Select(i => i.Title));
            Assert.True(vm.Items.Single(i => i.LinkId == selectedId).IsSelected);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }
}
