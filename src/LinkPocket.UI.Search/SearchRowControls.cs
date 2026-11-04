using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using LinkPocket.Contracts;
using LinkPocket.I18n;
using LinkPocket.ViewModels;

namespace LinkPocket.Views;

/// <summary>
/// 搜索行文本的关键词高亮。命中区间由 <see cref="TextMatch"/> 提供（与引擎同源），
/// 这里只把区间画成 Run——模板模式下单元格由行模板创建，不能再走 code-behind 的 CellFactory。
/// </summary>
public sealed class HighlightTextBlock : TextBlock
{
    public static readonly DependencyProperty RowTextProperty = DependencyProperty.Register(
        nameof(RowText), typeof(string), typeof(HighlightTextBlock),
        new PropertyMetadata(string.Empty, (d, _) => ((HighlightTextBlock)d).Rebuild()));

    public static readonly DependencyProperty HighlightQueryProperty = DependencyProperty.Register(
        nameof(HighlightQuery), typeof(string), typeof(HighlightTextBlock),
        new PropertyMetadata(string.Empty, (d, _) => ((HighlightTextBlock)d).Rebuild()));

    public static readonly DependencyProperty NormalBrushKeyProperty = DependencyProperty.Register(
        nameof(NormalBrushKey), typeof(string), typeof(HighlightTextBlock),
        new PropertyMetadata("App.Text.Primary", (d, _) => ((HighlightTextBlock)d).Rebuild()));

    public string RowText
    {
        get => (string)GetValue(RowTextProperty);
        set => SetValue(RowTextProperty, value);
    }

    public string HighlightQuery
    {
        get => (string)GetValue(HighlightQueryProperty);
        set => SetValue(HighlightQueryProperty, value);
    }

    public string NormalBrushKey
    {
        get => (string)GetValue(NormalBrushKeyProperty);
        set => SetValue(NormalBrushKeyProperty, value);
    }

    private void Rebuild()
    {
        Inlines.Clear();
        var text = RowText;
        if (string.IsNullOrEmpty(text)) return;

        var normalKey = NormalBrushKey;
        var ranges = TextMatch.Ranges(text, HighlightQuery ?? string.Empty);
        if (ranges.Count == 0)
        {
            Inlines.Add(ColoredRun(text, normalKey));
            return;
        }

        var pos = 0;
        foreach (var range in ranges)
        {
            if (range.Start > pos)
                Inlines.Add(ColoredRun(text[pos..range.Start], normalKey));
            var accent = ColoredRun(text.Substring(range.Start, range.Length), "App.Accent.Fill");
            accent.FontWeight = FontWeights.Bold;
            Inlines.Add(accent);
            pos = range.End;
        }
        if (pos < text.Length)
            Inlines.Add(ColoredRun(text[pos..], normalKey));
    }

    private static Run ColoredRun(string text, string key)
    {
        var run = new Run(text);
        run.SetResourceReference(TextElement.ForegroundProperty, key);
        return run;
    }
}

/// <summary>
/// 模板里的轻量文案单元格：值是 <see cref="LocValue"/>（键 + 参数 / 时刻），渲染时取词。
/// 相比 LocFit：不做可用宽探测、不订阅布局；语言版本变化时重取一次即可。
/// 搜索表格的列宽是冻结几何，日期列放不下由 TextTrimming 兜底（与工厂模式的显示口径一致）。
/// </summary>
public sealed class LocalizedTextBlock : TextBlock
{
    public static readonly DependencyProperty TextValueProperty = DependencyProperty.Register(
        nameof(TextValue), typeof(LocValue), typeof(LocalizedTextBlock),
        new PropertyMetadata(LocValue.Empty, (d, _) => ((LocalizedTextBlock)d).Refresh()));

    public LocValue TextValue
    {
        get => (LocValue)GetValue(TextValueProperty);
        set => SetValue(TextValueProperty, value);
    }

    public LocalizedTextBlock()
    {
        Loaded += (_, _) =>
        {
            Loc.Table.PropertyChanged -= OnLocaleChanged;
            Loc.Table.PropertyChanged += OnLocaleChanged;
        };
        Unloaded += (_, _) => Loc.Table.PropertyChanged -= OnLocaleChanged;
    }

    private void OnLocaleChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Refresh();

    private void Refresh() => Text = TextValue.Resolve();
}

/// <summary>
/// 搜索结果"位置"列：ListId → 目录路径（复用 SearchViewModel 注入的解析器）。
/// 第一路绑语言版本：路径里的虚根 token 随语言变化，版本一变必须重新取词。
/// </summary>
public sealed class SearchPathConverter : IMultiValueConverter
{
    public static SearchPathConverter Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 3 || values[1] is not SearchViewModel vm) return string.Empty;
        if (values.Length >= 4 && values[3] is true) return string.Empty;   // 占位行不显示“根目录”
        var listId = values[2] as string;
        var version = values[0] is int v ? v : 0;
        return vm.ResolveFolderPathText(string.IsNullOrEmpty(listId) ? null : listId, version);
    }

    public object[]? ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("search path projection is one-way.");
}
