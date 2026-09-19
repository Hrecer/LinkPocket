namespace LinkPocket.Contracts;

/// <summary>EngineClient · locate 域（1 命令：按 ID 解析位置——一切读取皆查询）。</summary>
public sealed partial class EngineClient
{
    /// <summary>
    /// 按 ID 解析目标位置（链接或文件夹）：返回类型 / **容器目录**（进它才能看到目标那一行）/
    /// 容器与目标的显示路径。界面的「跳转」= 切页 + 进容器 + 选中目标三个原语；
    /// ID 不存在 → <c>ENTITY_NOT_FOUND</c>（零兼容：查询就报错，不返回 null）。
    /// </summary>
    public Task<LocateResolveDto> LocateResolveAsync(string id, CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<LocateResolveDto>("locate.resolve", new { id }, o, ct);
}
