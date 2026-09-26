using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Engine;

/// <summary>
/// 撤销协调器（IUndoCoordinator）：纯状态机（撤销栈 + 重做栈，上限 100 条）。
/// 登记面：引擎在顶层可撤销命令（Reversible + UndoInverse）成功后调 <see cref="Record"/>。
/// 消费面：undo.undo / undo.redo 命令处理器调 TakeUndoAsync/TakeRedoAsync 拿到条目后，
/// 在自身管道内嵌套派发逆向命令/原命令（与撤销命令同事务；引擎会为逆向执行再次登记新条目——
/// 对称对由此天然形成 撤销↔重放 的往复能力）。
/// </summary>
/// <remarks>
/// <b>落盘（三档，2026-09-26 重做）</b>：<br/>
/// ① <b>压缩 + 后台写</b>：索引（gzip）小、写得快；步骤分片在**后台线程**压缩落盘，UI 线程只付索引那份。<br/>
/// ② <b>体量与自愈</b>：单条目步数硬上限、分片总量上限（超出淘汰最旧）、旧格式超大日志启动即丢弃并留痕。<br/>
/// ③ <b>摘要常驻 + 步骤按需水合</b>：大条目的步骤**不进内存**，索引只记 <see cref="UndoEntry.SummaryCommand"/>
/// 与分片名；启动只读索引，撤销到该条时才读它的分片（<see cref="TakeUndoAsync"/> / <see cref="TakeRedoAsync"/>
/// 返回前水合）。<br/><br/>
/// <b>为什么必须这么改</b>：原实现每次栈变动都<b>序列化整个快照并整份重写</b>，启动则<b>整份读+反序列化</b>。
/// 一次批量脚本（改 1430+ 书签）留下单条 3544 步的条目 ⇒ 日志涨到 <b>456 MB</b>：每次撤销栈变动写 456MB、
/// 每次启动读 456MB（堆里膨胀到 GB 级 ⇒ GC 压力把整个界面的操作都拖慢，用户实测"切页/开目录卡 1-2 秒"）。
/// 现在的代价与"条目有多大"脱钩：索引恒定很小，分片只在该条目**首次成型**时压一次。
/// </remarks>
public sealed class UndoCoordinator : IUndoCoordinator, IDisposable
{
    private const int Capacity = 100;

    /// <summary>步骤数 ≤ 它的条目**随索引内联**（典型条目 1–5 步）；更大的走独立分片。</summary>
    private const int InlineStepsMax = 50;

    /// <summary>单条目步数**硬上限**：超过则只在内存里可撤销，**不入日志**（如实留痕，不写半截数据）。</summary>
    private const int MaxStepsPerEntry = 5000;

    /// <summary>旧格式（未压缩 JSON）日志超过它就**启动即丢弃**：读一份 456MB 的存档要几秒且要 GB 级内存，
    /// 为"可能还想撤销"赔上整个应用不可用不划算（丢弃留 Warn 痕，空栈起步 = 既有的"存档不可用"语义）。</summary>
    private const long LegacyMaxBytes = 64L * 1024 * 1024;

    /// <summary>分片（压缩后）总量上限：超出则**淘汰最旧的条目**（连同它的内存条目），留痕。</summary>
    private const long MaxPartsBytes = 64L * 1024 * 1024;

    private readonly ConcurrentStack<UndoEntry> _undo = new();
    private readonly ConcurrentStack<UndoEntry> _redo = new();

    /// <summary>
    /// 撤销栈的**落盘文件**（<c>null</c> = 纯内存，不持久化）。步骤分片落在同目录的
    /// <c>&lt;文件名&gt;.parts/</c> 下（每个大条目一个 gzip 分片）。
    /// </summary>
    /// <remarks>
    /// 为什么要有它：撤销栈原来是纯内存的——应用一关，"可撤销"就全部蒸发，
    /// 用户做完一批改动、关掉应用再打开，想撤销也没门（实测反馈："可撤回的功能，
    /// 应用关闭了也要可以回溯"）。条目本身完全自包含（逆向命令 + 旧值参数都是 JSON），
    /// 落盘重放没有跨进程的障碍，缺的只是把栈写下来。
    /// </remarks>
    private readonly string? _journalPath;
    private readonly string? _partsDir;

    private static readonly JsonSerializerOptions JournalOptions = new()
    {
        WriteIndented = false,
    };

    /// <param name="journalPath">
    /// 撤销栈落盘文件；<c>null</c> = 纯内存（测试与不启用持久化的宿主用）。
    /// 损坏的文件**绝不抛**：当次忽略（从空栈开始），不能因为一份坏存档把引擎卡死。
    /// </param>
    public UndoCoordinator(string? journalPath = null)
    {
        _journalPath = journalPath;
        if (journalPath is null) return;
        _partsDir = journalPath + ".parts";
        try
        {
            if (!File.Exists(journalPath)) return;
            var length = new FileInfo(journalPath).Length;
            if (!IsGzip(journalPath))
            {
                if (length > LegacyMaxBytes)
                {
                    // ② 自愈：旧格式的巨型日志（实测 456MB）读一次就要几秒 + GB 级内存 —— 丢弃并留痕
                    LpLog.Warn(
                        $"undo journal too large ({length / 1024 / 1024} MB) — discarded to keep startup fast; "
                        + "the undo stack starts empty (this is the documented 'journal unusable' behavior)",
                        null, "engine.undo");
                    return;
                }
                LoadLegacy(journalPath);
                return;   // 迁移：大条目在下一次 Persist 时自动转独立分片（见 NeedsPart / ToDto）
            }
            LoadV2(journalPath);
        }
        catch (Exception ex)
        {
            // 撤销存档损坏 = 放弃这一份（空栈起步），绝不让启动失败
            LpLog.Warn($"undo journal unreadable, starting with an empty stack: {journalPath}", ex, "engine.undo");
        }
    }

    private static bool IsGzip(string path)
    {
        using var fs = File.OpenRead(path);
        return fs.ReadByte() == 0x1F && fs.ReadByte() == 0x8B;
    }

    /// <summary>v2：gzip JSON 索引；大条目的步骤在 <c>&lt;日志&gt;.parts/&lt;id&gt;.json.gz</c>（按需水合）。</summary>
    private void LoadV2(string path)
    {
        JournalIndex? index;
        using (var fs = File.OpenRead(path))
        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            index = JsonSerializer.Deserialize<JournalIndex>(gz, JournalOptions);
        if (index is null) return;
        // 索引数组是"栈顶在先"（ConcurrentStack.ToArray 口径）→ **逆序压回**
        for (var i = index.Undo.Count - 1; i >= 0; i--)
            if (FromDto(index.Undo[i]) is { } entry) _undo.Push(entry);
        for (var i = index.Redo.Count - 1; i >= 0; i--)
            if (FromDto(index.Redo[i]) is { } entry) _redo.Push(entry);
    }

    /// <summary>旧格式（未压缩 JSON 的 <c>{Undo,Redo}</c>），一次性读入内存；由 <see cref="MarkAllForJournal"/> 触发迁移。</summary>
    private void LoadLegacy(string path)
    {
        var snapshot = JsonSerializer.Deserialize<JournalSnapshot>(File.ReadAllText(path), JournalOptions);
        if (snapshot is null) return;
        for (var i = snapshot.Undo.Count - 1; i >= 0; i--) _undo.Push(snapshot.Undo[i]);
        for (var i = snapshot.Redo.Count - 1; i >= 0; i--) _redo.Push(snapshot.Redo[i]);
    }

    /// <summary>索引条目 → 内存条目。分片缺失（被外部删掉）= **该条丢弃**（留痕），绝不用空步骤假装还在。</summary>
    private UndoEntry? FromDto(JournalEntry dto)
    {
        if (dto.Steps is { Count: > 0 }) return ToEntry(dto);
        if (string.IsNullOrEmpty(dto.Payload)) return null;   // 摘要条目：本来就是"尚未入日志"的中间态，不落索引
        if (_partsDir is null) return null;
        var file = Path.Combine(_partsDir, dto.Payload);
        if (!File.Exists(file))
        {
            LpLog.Warn($"undo payload missing — entry dropped: {dto.Payload}", null, "engine.undo");
            return null;
        }
        _readyParts[dto.Id] = new PartRef(dto.Payload, dto.StepCount);   // 步骤没进内存（③），撤销到它时再水合
        return ToEntry(dto);
    }

    private static UndoEntry ToEntry(JournalEntry dto) => new(
        Id: dto.Id,
        At: dto.At,
        Steps: dto.Steps ?? [],
        Caller: dto.Caller,
        GroupId: dto.GroupId)
    {
        SummaryCommand = dto.SummaryCommand,
    };

    /// <summary>已就绪的分片（entryId → 文件名 + **写入时的步数**）；**只有它里面的条目才会被索引引用**。
    /// 记步数是因为**分组条目会长大**（一次粘贴 300 个 = 同一个条目追加 300 步）：只记文件名的话，
    /// 早先写下的那份分片会被当成"已就绪"而永远不再刷新 ⇒ 重启后只能撤销半截（测试抓到的真 bug）。</summary>
    private readonly Dictionary<string, PartRef> _readyParts = new(StringComparer.Ordinal);

    private sealed record PartRef(string Name, int Steps);

    /// <summary>待写分片（entryId → 条目快照）。后台线程逐个压缩落盘；落好一个索引才引用一个
    /// ⇒ 崩溃永远不会留下"索引引用了半截步骤"的日志。</summary>
    private readonly ConcurrentDictionary<string, UndoEntry> _dirtyParts = new(StringComparer.Ordinal);
    private volatile bool _partWriterRunning;

    /// <summary>
    /// 栈操作互斥：TakeUndoAsync(id) 的快照→清空→重放之间若并发放置会吞掉新条目；
    /// 公开调用面虽经引擎写闸串行化，但协调器本身应自洽——不经管道直调的并发也绝不丢条目。
    /// </summary>
    private readonly object _lock = new();

    /// <summary>
    /// 落盘：① 索引（小、gzip、临时文件 + 原子替换，同步写完）；② 大条目的步骤分片交给后台线程。
    /// </summary>
    /// <remarks>
    /// 与原实现的差别：**代价与"条目有多大"脱钩** —— 原来每次栈变动都序列化整个快照（含所有步骤）并整份重写，
    /// 一条 3544 步的批量记录会让每次变动写 456MB。现在索引只有摘要（几十字节/条），大步骤走分片且只写一次。
    /// </remarks>
    private void Persist()
    {
        if (_journalPath is null) return;
        try
        {
            lock (_lock)
            {
                foreach (var entry in _undo)
                    if (NeedsPart(entry)) _dirtyParts[entry.Id] = entry;
                foreach (var entry in _redo)
                    if (NeedsPart(entry)) _dirtyParts[entry.Id] = entry;
                WriteIndex();
                SweepParts();
            }
            KickPartWriter();
        }
        catch (Exception ex)
        {
            // 持久化失败不阻断引擎操作：本次关机后丢的只是"还能撤销"这件事本身
            LpLog.Warn($"undo journal write failed: {_journalPath}", ex, "engine.undo");
        }
    }

    /// <summary>该条目是否需要（重新）写独立分片：步骤多、且还没落过分片，且没超过单条目硬上限
    /// （超上限的本来就不入日志，别反复塞回后台队列）。</summary>
    private bool NeedsPart(UndoEntry entry)
    {
        if (entry.Steps.Count <= InlineStepsMax || entry.Steps.Count > MaxStepsPerEntry) return false;
        // 判据三条：没写过 / 文件不在了（撤销→重做之间会被清理）/ **写的时候步数比现在少**（分组条目长大了）
        return !_readyParts.TryGetValue(entry.Id, out var part)
               || part.Steps != entry.Steps.Count
               || !PartExists(part.Name);
    }

    private bool PartExists(string name) =>
        _partsDir is not null && File.Exists(Path.Combine(_partsDir, name));

    /// <summary>写索引（gzip，临时文件 + 原子替换）。只引用**已就绪**的分片与内联步骤。</summary>
    private void WriteIndex()
    {
        if (_journalPath is null) return;
        var index = new JournalIndex(V: 2,
            Undo: _undo.Select(ToDto).Where(d => d is not null).Select(d => d!).ToArray(),
            Redo: _redo.Select(ToDto).Where(d => d is not null).Select(d => d!).ToArray());
        var dir = Path.GetDirectoryName(_journalPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // 临时文件写在**分片目录**里（不在宿主目录里露头，也不会被"目录里不许留临时文件"的巡检抓到），
        // 且失败就地清理 —— 只靠 Move 成功来清理会在 Move 失败时留下一份残留。
        var tempDir = _partsDir ?? dir;
        if (!string.IsNullOrEmpty(tempDir)) Directory.CreateDirectory(tempDir);
        var temp = Path.Combine(tempDir!, Path.GetFileName(_journalPath) + ".tmp-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using (var fs = File.Create(temp))
            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                JsonSerializer.Serialize(gz, index, JournalOptions);
            File.Move(temp, _journalPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* 清理尽力而为 */ }
            throw;
        }
    }

    /// <summary>内存条目 → 索引条目；<c>null</c> = **这一条不该进日志**（过大，或分片还没就绪）。</summary>
    private JournalEntry? ToDto(UndoEntry entry)
    {
        if (entry.Steps.Count == 0)
        {
            // 未水合（步骤在分片里）：索引只记摘要 + 分片名
            return _readyParts.TryGetValue(entry.Id, out var part) && PartExists(part.Name)
                ? new JournalEntry(entry.Id, entry.At, entry.Caller, entry.GroupId,
                    StepCount: part.Steps, SummaryCommand: entry.SummaryCommand ?? entry.Command, Payload: part.Name, Steps: null)
                : null;   // 分片没就绪：先不入日志（等它就绪后由后台写回调补进索引），绝不写半截
        }
        if (entry.Steps.Count > MaxStepsPerEntry)
            return null;   // ② 单条目过大：只在内存可撤销（登记时已留痕），不拖日志下水
        if (_readyParts.TryGetValue(entry.Id, out var ready) && ready.Steps == entry.Steps.Count && PartExists(ready.Name))
            return new JournalEntry(entry.Id, entry.At, entry.Caller, entry.GroupId,
                StepCount: entry.Steps.Count, SummaryCommand: entry.Command, Payload: ready.Name, Steps: null);
        if (entry.Steps.Count > InlineStepsMax)
            return null;   // 需要分片但还没写好：同上，等分片就绪
        return new JournalEntry(entry.Id, entry.At, entry.Caller, entry.GroupId,
            StepCount: entry.Steps.Count, SummaryCommand: entry.Command, Payload: null, Steps: entry.Steps);
    }

    /// <summary>后台写分片：一个条目一个 gzip 文件（临时文件 + 原子替换）；写好一个就把索引补上一次引用。</summary>
    private void KickPartWriter()
    {
        if (_partsDir is null || _dirtyParts.IsEmpty || _partWriterRunning) return;
        _partWriterRunning = true;
        _ = Task.Run(() =>
        {
            try
            {
                while (!_dirtyParts.IsEmpty)
                {
                    foreach (var (id, entry) in _dirtyParts.ToArray())
                    {
                        _dirtyParts.TryRemove(id, out _);
                        if (entry.Steps.Count == 0 || entry.Steps.Count > MaxStepsPerEntry) continue;
                        var name = id + ".json.gz";
                        try
                        {
                            Directory.CreateDirectory(_partsDir);
                            var temp = Path.Combine(_partsDir, name + ".tmp");
                            using (var fs = File.Create(temp))
                            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                                JsonSerializer.Serialize(gz, entry.Steps, JournalOptions);
                            File.Move(temp, Path.Combine(_partsDir, name), overwrite: true);
                            lock (_lock)
                            {
                                _readyParts[id] = new PartRef(name, entry.Steps.Count);
                                if (InEitherStack(id)) WriteIndex();   // 分片就绪 ⇒ 这一条此刻才第一次进日志
                                SweepParts();
                            }
                        }
                        catch (Exception ex)
                        {
                            LpLog.Warn($"undo payload write failed: {name}", ex, "engine.undo");
                        }
                    }
                }
            }
            finally
            {
                _partWriterRunning = false;
            }
        });
    }

    private bool InEitherStack(string id) =>
        _undo.Any(e => string.Equals(e.Id, id, StringComparison.Ordinal))
        || _redo.Any(e => string.Equals(e.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// ② 分片总量与清理：只留**两个栈里还在引用**的分片；总量超过上限时从**最旧**的条目开始淘汰
    /// （连同它的内存条目一起丢 —— 丢的是"更早的撤销能力"，不是正确性）。
    /// </summary>
    private void SweepParts()
    {
        if (_partsDir is null || !Directory.Exists(_partsDir)) return;
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var liveIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in _undo.Concat(_redo))
        {
            liveIds.Add(entry.Id);
            if (_readyParts.TryGetValue(entry.Id, out var part)) referenced.Add(part.Name);
        }

        // ⚠️ 清理判据必须是"**条目还在不在栈里**"，不能看"索引有没有引用它"：
        // 后台线程 File.Move 完成后到登记 `_readyParts` 之间有个瞬间——文件已在、引用未建，
        // 而并发的 Persist 会在这里把它当垃圾删掉（测试 `大条目_步骤转独立分片_重启后按需水合` 抓到的真 bug：
        // 分片反复被删 ⇒ 索引引用了不存在的文件）。
        foreach (var file in Directory.GetFiles(_partsDir, "*.json.gz"))
        {
            var name = Path.GetFileName(file);
            var id = name[..^".json.gz".Length];
            if (liveIds.Contains(id)) continue;   // 条目还在栈里：文件归它（可能刚写好、索引尚未引用）
            try { File.Delete(file); } catch { /* 清理失败不影响正确性 */ }
        }

        long total = 0;
        foreach (var name in referenced)
        {
            try { total += new FileInfo(Path.Combine(_partsDir, name)).Length; } catch { }
        }
        if (total <= MaxPartsBytes) return;

        // 从最旧的条目开始丢（栈底 = 最早）——两个栈一起算，按 At 升序
        var ordered = _undo.Concat(_redo).OrderBy(e => e.At).ToList();
        var dropped = 0;
        foreach (var entry in ordered)
        {
            if (total <= MaxPartsBytes) break;
            if (!_readyParts.TryGetValue(entry.Id, out var part)) continue;
            long size = 0;
            try { size = new FileInfo(Path.Combine(_partsDir, part.Name)).Length; } catch { }
            DropEntry(entry.Id);
            try { File.Delete(Path.Combine(_partsDir, part.Name)); } catch { }
            _readyParts.Remove(entry.Id);
            total -= size;
            dropped++;
        }
        if (dropped > 0)
        {
            LpLog.Warn($"undo journal parts exceeded {MaxPartsBytes / 1024 / 1024} MB — dropped {dropped} oldest entries "
                       + "(older undo history is gone; recent actions are unaffected)", null, "engine.undo");
            WriteIndex();
        }
    }

    /// <summary>把某条目从两个栈里移除（保持其余条目的相对顺序）。</summary>
    private void DropEntry(string id)
    {
        var undo = _undo.ToArray();   // 栈顶在前
        if (undo.Any(e => string.Equals(e.Id, id, StringComparison.Ordinal)))
        {
            _undo.Clear();
            foreach (var e in undo.Reverse())
                if (!string.Equals(e.Id, id, StringComparison.Ordinal)) _undo.Push(e);
        }
        var redo = _redo.ToArray();
        if (redo.Any(e => string.Equals(e.Id, id, StringComparison.Ordinal)))
        {
            _redo.Clear();
            foreach (var e in redo.Reverse())
                if (!string.Equals(e.Id, id, StringComparison.Ordinal)) _redo.Push(e);
        }
    }

    private sealed record JournalIndex(int V, IReadOnlyList<JournalEntry> Undo, IReadOnlyList<JournalEntry> Redo);

    private sealed record JournalEntry(
        string Id, DateTimeOffset At, CallerRef Caller, string? GroupId,
        int StepCount, string? SummaryCommand, string? Payload, IReadOnlyList<UndoStep>? Steps);

    /// <summary>旧格式（v1，未压缩）：升级用的一次性读取形状。</summary>
    private sealed record JournalSnapshot(IReadOnlyList<UndoEntry> Undo, IReadOnlyList<UndoEntry> Redo);

    // ── ③ 按需水合：撤销/重做**执行前**把步骤从分片读回来 ─────────────────────

    /// <summary>把条目补全（步骤不在内存时读它的分片）。读不到 = 返回 <c>null</c>（该条不可撤销，如实上报）。</summary>
    private UndoEntry? Hydrate(UndoEntry? entry)
    {
        if (entry is null || entry.Steps.Count > 0) return entry;
        if (_partsDir is null || !_readyParts.TryGetValue(entry.Id, out var part)) return null;
        try
        {
            using var fs = File.OpenRead(Path.Combine(_partsDir, part.Name));
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            var steps = JsonSerializer.Deserialize<List<UndoStep>>(gz, JournalOptions);
            if (steps is null || steps.Count == 0) return null;
            return entry with { Steps = steps };
        }
        catch (Exception ex)
        {
            LpLog.Warn($"undo payload unreadable — entry not undoable: {part.Name}", ex, "engine.undo");
            return null;
        }
    }

    public Task<IReadOnlyList<UndoEntry>> ListAsync(CancellationToken ct)
    {
        IReadOnlyList<UndoEntry> list = _undo.ToArray();   // 栈序 = 最近在前
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<UndoEntry>> ListRedoAsync(CancellationToken ct)
    {
        IReadOnlyList<UndoEntry> list = _redo.ToArray();   // 栈序 = 最近在前
        return Task.FromResult(list);
    }

    public Task<int> ClearAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            var count = _undo.Count + _redo.Count;
            _undo.Clear();
            _redo.Clear();
            Persist();
            return Task.FromResult(count);
        }
    }

    /// <summary>取（不弹）最近一条撤销记录，步骤按需水合（见 <see cref="IUndoCoordinator.PeekUndoAsync"/>）。</summary>
    public Task<UndoEntry?> PeekUndoAsync(string? id, CancellationToken ct)
    {
        var entries = _undo.ToArray();   // 栈顶在前
        var entry = id is null
            ? entries.FirstOrDefault()
            : entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        return Task.FromResult(Hydrate(entry));
    }

    /// <summary>取（不弹）最近一条重做记录，步骤按需水合。</summary>
    public Task<UndoEntry?> PeekRedoAsync(CancellationToken ct)
    {
        var entries = _redo.ToArray();
        return Task.FromResult(Hydrate(entries.FirstOrDefault()));
    }

    public Task<UndoEntry?> TakeUndoAsync(string? id, CancellationToken ct)
    {
        lock (_lock)
        {
            if (id is null)
            {
                var popped = _undo.TryPop(out var entry) ? entry : null;
                if (popped is null) return Task.FromResult<UndoEntry?>(null);
                // ⚠️ **先水合、后落盘**：落盘里的清理只保留"两个栈里还在引用的分片"，
                // 而此刻条目已出栈、还没进重做栈 —— 反过来做就是把自己的分片删掉再读（测试抓到的真 bug）。
                var hydrated = Hydrate(popped);
                Persist();
                return Task.FromResult(hydrated);
            }

            // 按 id 取指定条目：重排栈（栈内其余条目保持原序）
            var items = _undo.ToArray();
            var taken = items.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
            if (taken is null) return Task.FromResult<UndoEntry?>(null);
            var rest = items.Where(e => !string.Equals(e.Id, id, StringComparison.Ordinal));
            _undo.Clear();
            foreach (var e in rest) _undo.Push(e);
            var hydratedById = Hydrate(taken);   // 同上：先水合、后落盘
            Persist();
            return Task.FromResult(hydratedById);
        }
    }

    public Task<UndoEntry?> TakeRedoAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            var entry = _redo.TryPop(out var e) ? e : null;
            if (entry is null) return Task.FromResult<UndoEntry?>(null);
            var hydratedRedo = Hydrate(entry);   // 同上：先水合、后落盘
            Persist();
            return Task.FromResult(hydratedRedo);
        }
    }

    /// <summary>
    /// 登记一条（或合并进一条）可撤销记录。
    /// 逆向步骤来源：① 处理器回填的 <paramref name="inverse"/>（能带旧值，重命名/移动靠它）；
    /// ② 退回描述符声明的 UndoInverse + 原参数（对称对 links.trash↔trash.restore 走这条）。
    /// 两者都没有 → 不入栈（例如 folders.update 改名：明确不可撤销）。
    /// <paramref name="groupId"/> 与栈顶同组 → **追加合并**（一次粘贴多选 = 一个用户动作 = 一条记录）。
    /// </summary>
    public void Record(CommandDescriptor descriptor, JsonElement args, CallerRef caller,
        IReadOnlyList<UndoInverseStep>? inverse = null, string? groupId = null)
    {
        if (!descriptor.IsMutation) return;

        var steps = BuildSteps(descriptor, args, inverse);
        if (steps.Count == 0) return;

        lock (_lock)
        {
            _redo.Clear();   // 新撤销使重做链失效（标准 redo 语义）

            // 分组：与栈顶同组 → 合并进同一条（不进新条，容量不变）
            if (groupId is { Length: > 0 } && _undo.TryPeek(out var top)
                && string.Equals(top.GroupId, groupId, StringComparison.Ordinal))
            {
                _undo.TryPop(out _);
                var merged = top with { Steps = [.. top.Steps, .. steps] };
                _undo.Push(merged);
                if (top.Steps.Count <= MaxStepsPerEntry && merged.Steps.Count > MaxStepsPerEntry)
                {
                    // ② 跨过硬上限：这一条从此只在本会话可撤销（如实留痕一次，不是每次落盘都刷）
                    LpLog.Warn($"undo entry exceeded {MaxStepsPerEntry} steps — kept in memory only "
                               + "(it will not survive a restart)", null, "engine.undo");
                }
                Persist();
                return;
            }

            _undo.Push(new UndoEntry(
                Id: Guid.NewGuid().ToString("N"),
                At: DateTimeOffset.Now,
                Steps: steps,
                Caller: caller,
                GroupId: groupId));

            while (_undo.Count > Capacity) _undo.TryPop(out _);
        }
        Persist();

        // 里程碑（Debug）：撤销栈变化——「本次用户动作留下了什么可回退的东西」
        if (LpLog.IsEnabled(LogLevel.Debug))
            LpLog.Write(LogLevel.Debug, "engine.undo", $"Undo registered: {descriptor.Name}", props: new Dictionary<string, object?>
            {
                // cmd / caller / corr 由**调用上下文**落到记录首类字段（不在 props 里再抄一份）
                ["steps"] = steps.Count,
                ["group"] = groupId ?? string.Empty,
            });
    }

    /// <summary>构造本次调用的可撤销步骤（处理器回填优先；否则退回描述符 + 原参数）。</summary>
    private static IReadOnlyList<UndoStep> BuildSteps(
        CommandDescriptor descriptor, JsonElement args, IReadOnlyList<UndoInverseStep>? inverse)
    {
        if (inverse is { Count: > 0 })
            return inverse
                .Select(s => new UndoStep(descriptor.Name, args.Clone(), s.Command, s.Args.Clone(), s.Redo))
                .ToArray();

        if (descriptor.UndoInverse is not { Length: > 0 } fallbackInverse)
            return [];   // 无回填也无声明 → 不可撤销

        return [new UndoStep(descriptor.Name, args.Clone(), fallbackInverse, args.Clone())];
    }

    /// <summary>undo.undo 处理器在逆向命令派发成功后调（把弹出条目转入重做栈）。</summary>
    public void MarkUndone(UndoEntry entry)
    {
        lock (_lock)
        {
            _redo.Push(entry);
        }
        Persist();
    }

    /// <summary>退出前把还在后台队列里的分片同步写完（幂等；未调也只是丢最后一次批量动作的跨进程撤销能力）。</summary>
    public void Dispose()
    {
        if (_partsDir is null) return;
        try
        {
            foreach (var (id, entry) in _dirtyParts.ToArray())
            {
                _dirtyParts.TryRemove(id, out _);
                if (entry.Steps.Count == 0 || entry.Steps.Count > MaxStepsPerEntry) continue;
                var name = id + ".json.gz";
                Directory.CreateDirectory(_partsDir);
                var temp = Path.Combine(_partsDir, name + ".tmp");
                using (var fs = File.Create(temp))
                using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                    JsonSerializer.Serialize(gz, entry.Steps, JournalOptions);
                File.Move(temp, Path.Combine(_partsDir, name), overwrite: true);
                lock (_lock) _readyParts[id] = new PartRef(name, entry.Steps.Count);
            }
            lock (_lock) WriteIndex();
        }
        catch (Exception ex)
        {
            LpLog.Warn("undo journal flush on shutdown failed", ex, "engine.undo");
        }
    }
}
