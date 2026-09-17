namespace LinkPocket.Data;

/// <summary>
/// 实体 ID 唯一生成入口（Folder / Link 的主键都是不透明 TEXT，业务代码不得按 ID 形状做判断，
/// <see cref="LooksLikeFolderId"/> 仅供诊断/调试显示，不参与业务分支）。
///
/// <para>格式定稿（2026-09-17）：</para>
/// <para>· 文件夹 = <b>固定 12 位纯数字</b>随机串（空间 10^12）；</para>
/// <para>· 链接　 = <b>固定 16 位大小写字母+数字混合</b>随机串（空间 62^16）。</para>
///
/// <para>两类固定位数不同、字符集不同，人眼一眼可分。碰撞为宇宙级事件：
/// 1 万个文件夹碰撞概率约十万分之五；即便真撞上，SQLite 主键约束让插入直接报错而非静默坏数据，
/// <see cref="LinkPocketDbContext"/> 保存层会捕获该冲突并换号重试一次。</para>
/// </summary>
public static class EntityIds
{
    private const string LinkAlphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    /// <summary>文件夹主键：固定 12 位纯数字。</summary>
    public static string NewFolderId()
        => new string(Enumerable.Range(0, 12).Select(_ => (char)('0' + Random.Shared.Next(10))).ToArray());

    /// <summary>链接主键：固定 16 位大小写字母+数字混合。</summary>
    public static string NewLinkId()
        => new string(Enumerable.Range(0, 16).Select(_ => LinkAlphabet[Random.Shared.Next(LinkAlphabet.Length)]).ToArray());

    /// <summary>是否为现行文件夹 ID 格式（12 位纯数字）。仅供诊断显示；历史数据（19 位数字）与
    /// 回收站合成 ID 不满足此判定属正常，业务代码不得据此分支。</summary>
    public static bool LooksLikeFolderId(string id)
        => id.Length == 12 && id.All(char.IsDigit);
}
