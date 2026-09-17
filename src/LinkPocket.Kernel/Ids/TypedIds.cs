namespace LinkPocket.Kernel;

/// <summary>强类型文件夹 ID（编译期杜绝"按 ID 形状猜类型"，方案 4.1）。</summary>
public readonly record struct FolderId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>强类型链接 ID。</summary>
public readonly record struct LinkId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>强类型回收站单元 ID。</summary>
public readonly record struct TrashFolderId(string Value)
{
    public override string ToString() => Value;
}
