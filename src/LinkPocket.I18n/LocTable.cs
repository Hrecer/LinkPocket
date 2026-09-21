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
    /// <remarks>
    /// <b>换入顺序是硬约束</b>：先把 <c>Locale</c> 与 <c>_map</c> 都换成新表，再发通知。
    /// 反过来（先发通知再换表）会让监听方在"语言已变、取词表还是旧的"半成品状态下干活——
    /// 实测症状 = 监听方按新语言去驱动重取，当场读回的仍是旧语言文本，
    /// 而屏幕上就一直留着上一种语言（且"手动再驱动一次"立刻正常，因为那时表已经换完了）。
    /// </remarks>
    internal void Reload(AppLocale target)
    {
        if (Locale == target) return;

        // ① 先换表（两件事都做完才算"语言换了"）
        var next = StringTables.For(target);
        _map.Clear();
        foreach (var (key, text) in next) _map[key] = text;
        _warned.Clear();
        Locale = target;
        Version++;

        // ② 再通知（此刻任何监听方读到的都已经是新语言的完整状态）
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
            LpLog.Warn($"missing copy key: {key} (locale={Locale.CodeOf()})", category: LogCategory);
        return "⟨" + key + "⟩";
    }
}
