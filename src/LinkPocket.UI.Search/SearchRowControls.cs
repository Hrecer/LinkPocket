using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using LinkPocket.Contracts;

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
