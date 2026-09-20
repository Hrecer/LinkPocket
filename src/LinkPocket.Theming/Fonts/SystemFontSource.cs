using System.Collections.ObjectModel;

namespace LinkPocket.Theming.Fonts;

/// <summary>一个可选字体族（系统已装 或 用户导入）。</summary>
/// <param name="Family">WPF 字体族名（令牌链的第一段）。</param>
/// <param name="DisplayName">界面上显示的名字（导入字体为"文件名 · 族名"）。</param>
/// <param name="FilePath">导入字体的文件绝对路径；<c>null</c> = 系统已装字体。</param>
public sealed record FontChoice(string Family, string DisplayName, string? FilePath = null)
{
    /// <summary>是否来自用户导入的文件（可删除）。</summary>
    public bool IsImported => FilePath is not null;
}

/// <summary>
/// 系统已装字体的**来源**（WPF <c>Fonts.SystemFontFamilies</c> 的抽象）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要有一个接口而不是直接调 WPF</b>：这是"系统字体集合"这件事的**唯一事实来源**，
/// 而它的取值来源必须可分派 —— 生产 = WPF 全量枚举；测试 = 注入固定列表。
/// 没有这条缝时，想验证"候选投影 / 可用性判定"就只能真的去枚举本机字体，
/// 用例的结果会随"这台机器装了什么字体"而变（本仓最忌讳的"测试依赖环境"）。
/// </para>
/// <para>
/// <b>真实枚举路径仍然有自动化覆盖</b>（不是只测假列表）：
/// <c>FontSystemTests.系统字体枚举_非空且按显示名排序</c> 与
/// <c>字体候选_界面与等宽都有候选_且投影当前字体</c> 都直接走本接口的生产实现。
/// </para>
/// </remarks>
public interface ISystemFontSource
{
    /// <summary>枚举系统已装字体族（按显示名升序；只返回可与 WPF 解析的项）。</summary>
    IReadOnlyList<FontChoice> Enumerate();
}

/// <summary>
/// 生产实现：WPF <c>Fonts.SystemFontFamilies</c> 的**全量枚举**（一次，实例内缓存）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须缓存</b>：全量枚举要走 GDI/COM 字体表，在装了大量字体的机器上是**秒级**开销；
/// 不缓存时每构建一次「外观」面板 VM 就枚举一次，真实用户每进一次「外观」页也要等同样久。
/// </para>
/// <para>
/// <b>缓存为什么在进程内是安全的</b>：系统字体集合不会在应用运行期间变化。
/// 用户新装的字体要重启应用才可见（与绝大多数桌面应用一致，属可接受边界）；
/// 导入字体走另一条路（<see cref="FontCatalog.ImportedFonts"/>，每次读目录 → 导入/删除即时可见）。
/// </para>
/// <para>
/// <b>枚举本身是纯计算</b>（<see cref="FontChoice"/> 只是数据 record，不带
/// <see cref="System.Windows.Threading.DispatcherObject"/> 亲和性），
/// 所以可以安全地在后台线程跑 —— <see cref="FontCatalog.LoadAsync"/> 正是这么做的。
/// </para>
/// </remarks>
public sealed class WpfSystemFontSource : ISystemFontSource
{
    private readonly Lazy<IReadOnlyList<FontChoice>> _cache;

    /// <summary>构造一个带缓存的枚举器（每个实例自带一份缓存）。</summary>
    public WpfSystemFontSource() =>
        _cache = new Lazy<IReadOnlyList<FontChoice>>(EnumerateCore, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <inheritdoc />
    public IReadOnlyList<FontChoice> Enumerate() => _cache.Value;

    private static IReadOnlyList<FontChoice> EnumerateCore()
    {
        var list = new List<FontChoice>();
        foreach (var family in System.Windows.Media.Fonts.SystemFontFamilies)
        {
            // 一个字体族有多个本地化名（如 "Microsoft YaHei UI" / "微软雅黑 UI"）——取第一个作为显示名
            var name = family.FamilyNames.Values.FirstOrDefault() ?? family.Source;
            list.Add(new FontChoice(family.Source, name));
        }
        list.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCulture));
        return new ReadOnlyCollection<FontChoice>(list);
    }
}
