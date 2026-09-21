using System;
using System.Collections.Generic;
using System.ComponentModel;
using LinkPocket.Contracts;

namespace LinkPocket.I18n;

/// <summary>
/// 当前语言的取词表 + 版本号。<see cref="Version"/> 一变，所有经 <see cref="LocExtension"/> 取词的绑定
/// 自动重算——这就是"切语言不重启"的唯一机制（不存在第二套生效路径，也不需要任何 VM 订阅）。
/// </summary>
public sealed class LocTable : INotifyPropertyChanged
{
    private const string LogCategory = "i18n";

    /// <summary>全局唯一实例（markup extension 经 <c>x:Static</c> 指它）。</summary>
    public static LocTable Instance { get; } = new();

    private readonly Dictionary<string, string> _map;
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    private LocTable()
    {
        _map = new Dictionary<string, string>(StringTables.For(AppLocales.Default), StringComparer.Ordinal);
        Locale = AppLocales.Default;
    }

    public AppLocale Locale { get; private set; }

    /// <summary>语言代数，只增不减；取词绑定挂在它上面失效。</summary>
    public int Version { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>整表一次换入（只允许 <see cref="LocaleService"/> 调，避免出现第二个生效入口）。</summary>
    internal void Reload(AppLocale target)
    {
        if (Locale == target) return;

        var next = StringTables.For(target);
        _map.Clear();
        foreach (var (key, text) in next) _map[key] = text;
        _warned.Clear();
        Locale = target;
        Version++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version)));
    }

    public bool TryGet(string key, out string text)
    {
        var hit = _map.TryGetValue(key, out var found);
        text = found ?? string.Empty;
        return hit;
    }

    /// <summary>
    /// 取词。<b>缺键不静默</b>：返回 <c>⟨key⟩</c> 让它在界面上显形，并按键去重写一条 Warn
    /// （静默回退成空串 = 用户只看到一个空白框，是本仓禁止的观测面缺陷）。
    /// </summary>
    public string Get(string key)
    {
        if (_map.TryGetValue(key, out var text)) return text;
        if (_warned.Add(key))
            LpLog.Warn($"缺文案键：{key}（语言={Locale.CodeOf()}）", category: LogCategory);
        return "⟨" + key + "⟩";
    }
}
