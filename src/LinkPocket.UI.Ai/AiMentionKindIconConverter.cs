using System.Globalization;
using System.Windows.Data;
using LinkPocket.Contracts;

namespace LinkPocket.UI.Ai;

/// <summary>提及候选的类型图标：文件夹 = <c>folder</c>、链接 = <c>link-variant</c>（只认已注册字形）。</summary>
public sealed class AiMentionKindIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is AiMentionKind.Link ? "link-variant" : "folder";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
