using System.Text;

namespace LinkPocket.Ai;

/// <summary>
/// AI 数据文件的**原子写唯一实现**：同目录临时文件 + 原子替换（**绝不预删目标**，绝不直接覆写），
/// UTF-8 无 BOM。与外观偏好（<c>ui-preferences.json</c>）同一口径；失败如实抛，由调用方暴露。
/// <para>替换步骤带**有界重试**：Windows 的 <c>File.Replace</c>/<c>ReplaceFile</c> 在文件系统压力下
/// （沙箱 overlay、杀软/索引器的瞬时句柄）会抛出与真实失败无法区分的瞬态 <see cref="IOException"/>
/// （连异常文案都可能自相矛盾，如"操作成功完成"）——这是环境噪声而非数据错误；重试后仍失败才如实上抛。
/// 与 <c>build/ci.ps1</c> 编译门对同类瞬态占用的重试同一口径。</para>
/// </summary>
public static class AtomicFile
{
    /// <summary>替换步骤的最大尝试次数（含首次）。</summary>
    private const int ReplaceAttempts = 5;

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

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(path))
                    File.Replace(tmp, path, destinationBackupFileName: null);
                else
                    File.Move(tmp, path);
                return;
            }
            catch (IOException) when (attempt < ReplaceAttempts)
            {
                // 上一轮若在半途消费掉临时文件（Move 成功后被判定失败等），重写一份再试
                if (!File.Exists(tmp))
                    File.WriteAllText(tmp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                Thread.Sleep(20 * attempt);
            }
        }
    }
}
