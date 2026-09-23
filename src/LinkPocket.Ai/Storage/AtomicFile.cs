using System.Text;

namespace LinkPocket.Ai;

/// <summary>
/// AI 数据文件的**原子写唯一实现**：同目录临时文件 + 原子替换（**绝不预删目标**，绝不直接覆写），
/// UTF-8 无 BOM。与外观偏好（<c>ui-preferences.json</c>）同一口径；失败如实抛，由调用方暴露。
/// </summary>
public static class AtomicFile
{
    /// <summary>读（文件不存在 → null；其它失败不吞）。</summary>
    public static string? TryReadAllText(string path)
        => File.Exists(path) ? File.ReadAllText(path) : null;

    /// <summary>原子替换写：先写 <c>&lt;path&gt;.tmp</c>，再 <c>File.Replace</c>（目标不存在时 <c>File.Move</c>）。</summary>
    public static void WriteAllText(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (File.Exists(path))
            File.Replace(tmp, path, destinationBackupFileName: null);
        else
            File.Move(tmp, path);
    }
}
