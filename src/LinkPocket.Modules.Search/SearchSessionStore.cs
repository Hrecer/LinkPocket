using System.Collections.Concurrent;
using LinkPocket.Contracts;
using LinkPocket.Kernel;

namespace LinkPocket.Modules.Search;

/// <summary>
/// 搜索会话的**单一快照**持有者：每个会话独占一个 UoW + SQLite 读事务，
/// count 与后续所有页都在同一事务里读取；写入方（WAL）可以继续提交，
/// 但本会话不会看到页间变化。
/// </summary>
/// <remarks>
/// 会话只在“界面一次搜索的补全周期”内存在（~1s），由搜索页在完成/取消时显式关闭；
/// TTL 只是防泄漏兜底（异常退出/界面忘记关闭），不能让长事务永久占住 WAL。
/// </remarks>
internal sealed class SearchSessionStore
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultSweepPeriod = TimeSpan.FromSeconds(30);

    /// <summary>同时存活的快照会话上限（每个会话独占一个连接 + 读事务）。界面只用 1 个；
    /// 其余额度留给 AI/CLI 宿主与异常退出的短暂重叠，超出时淘汰最久未用的空闲会话。</summary>
    private const int MaxSessions = 16;

    private readonly Func<IUnitOfWork> _uowFactory;
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _sweepPeriod;
    private readonly System.Threading.Timer _sweepTimer;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public SearchSessionStore(Func<IUnitOfWork> uowFactory, TimeSpan? ttl = null, TimeSpan? sweepPeriod = null)
    {
        _uowFactory = uowFactory;
        _ttl = ttl ?? DefaultTtl;
        _sweepPeriod = sweepPeriod ?? DefaultSweepPeriod;
        // 只在有会话时激活（one-shot 自续期）：没有会话就没有后台扫表，也没有需要释放的定时器。
        _sweepTimer = new System.Threading.Timer(
            _ => SweepExpired(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public async Task<SearchSessionOpenResult> OpenAsync(
        string query, bool searchTitle, bool searchUrl, bool searchDescription, bool searchPath,
        string sortBy, string sortOrder, int firstPageSize, bool includeFolders, CancellationToken ct)
    {
        SweepExpired();
        await EvictForCapacityAsync();

        var uow = _uowFactory();
        var tx = uow.BeginReadTransaction();
        try
        {
            await tx.BeginAsync(ct);
            // 目录命中在 SQL 侧算一次（名称命中 + 严格后代），位置范围的子树展开与文件夹块共用它：
            // 十万目录时全量物化 ≈410ms，只取命中行是 20–200ms（见 WARNINGS 245）。
            // 目录命中只查一次：标题档取「名称命中」，位置档另展开「严格后代」（与链接作用域共用）。
            // ⚠️ 必须与 count/取页在**同一个读事务**里：曾用第二个 UoW 把目录命中与链接计数并行，
            // 两条连接各看各的快照，写并发时会拼出"总数与目录行不对应"（见 WARNINGS 219/247）。
            var needsFolders = includeFolders || searchPath;
            FolderNameSearch? folderHits = null;
            if (needsFolders && !string.IsNullOrWhiteSpace(query))
            {
                folderHits = await uow.Folders.SearchByNameAsync(
                    query, includeDescendants: searchPath, sortBy, ct);
            }

            var scope = SearchSupport.BuildScope(
                query, searchTitle, searchUrl, searchDescription, searchPath, folderHits);

            // 文件夹与链接在同一条结果流里：文件夹块固定排在链接块之前（各自按当前排序键排）。
            // 只在"标题/位置"两档下命中——URL/描述对文件夹没有意义（见 SearchSupport）。
            var folders = includeFolders && folderHits is not null
                ? SearchSupport.BuildFolderRows(folderHits, searchTitle, searchPath)
                : [];
            if (folders.Count > 0) SearchSupport.SortFolderHits(folders, sortBy, sortOrder);

            int total;
            List<LinkDto> firstPage;
            if (folders.Count > 0)
            {
                // 有文件夹命中 ⇒ 拼接分页（链接段起点 = "全局偏移 − 文件夹数"）。
                // 首页需要总数：BuildPageAsync 让链接段**一次扫描同时计数 + 收窗口**；
                // 文件夹块就填满首页时，只需为总数数一次链接（仍比 count + 取页两扫便宜）。
                if (firstPageSize > 0)
                {
                    var page = await SearchSupport.BuildPageAsync(
                        uow, scope, folders, sortBy, sortOrder, page: 1, perPage: firstPageSize,
                        needLinkTotal: true, ct);
                    total = page.LinkTotal + folders.Count;
                    firstPage = page.Rows;
                }
                else
                {
                    total = await SearchSupport.CountAsync(uow, scope, ct) + folders.Count;
                    firstPage = [];
                }
            }
            else if (firstPageSize > 0)
            {
                // 首屏快路径：纯文本 page-first / path 单扫（仓储内部按 scope 选路），同一事务。
                var first = await uow.Links.CountAndFirstPageAsync(
                    scope, sortBy, sortOrder, firstPageSize, skip: 0, ct);
                total = first.Total;
                firstPage = first.FirstPage.ToList();
            }
            else
            {
                total = await SearchSupport.CountAsync(uow, scope, ct);
                firstPage = [];
            }

            var sessionId = Guid.NewGuid().ToString("N");
            var session = new Session(scope, sortBy, sortOrder, uow, tx, folders);
            if (!_sessions.TryAdd(sessionId, session))
                throw new InvalidOperationException("search snapshot session id collision");
            ScheduleSweep();
            return new SearchSessionOpenResult(sessionId, total, firstPage);
        }
        catch
        {
            await tx.DisposeAsync();
            await uow.DisposeAsync();
            throw;
        }
    }

    public async Task<IReadOnlyList<LinkDto>> PageAsync(
        string sessionId, int page, int perPage, CancellationToken ct)
    {
        SweepExpired();
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound,
                $"search snapshot session '{sessionId}' does not exist or has expired"));

        await session.Gate.WaitAsync(ct);
        try
        {
            session.Touch();
            if (session.Folders.Count > 0)
            {
                var result = await SearchSupport.BuildPageAsync(
                    session.Uow, session.Scope, session.Folders, session.SortBy, session.SortOrder,
                    page, perPage, needLinkTotal: false, ct);
                return result.Rows;
            }

            var hits = await SearchSupport.SearchAsync(
                session.Uow, session.Scope, session.SortBy, session.SortOrder, ct, page, perPage);
            return hits.Select(h => h.Link).ToList();
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>幂等关闭：不存在视为已关闭（界面取消防抖时不需要区分“刚关过”）。</summary>
    public async Task<bool> CloseAsync(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            StopSweepIfIdle();
            return false;
        }
        await DisposeSessionAsync(session);
        StopSweepIfIdle();
        return true;
    }

    /// <summary>会话上限兜底：只淘汰**空闲**（没有页在读取）的最久未用会话；全忙则如实拒绝新会话。</summary>
    private async Task EvictForCapacityAsync()
    {
        while (_sessions.Count >= MaxSessions)
        {
            KeyValuePair<string, Session>? victim = null;
            foreach (var pair in _sessions)
            {
                if (pair.Value.Gate.CurrentCount == 0) continue;
                if (victim is null || pair.Value.LastAccess < victim.Value.Value.LastAccess)
                    victim = pair;
            }
            if (victim is null)
                throw new EngineException(EngineErrors.Of(EngineErrors.DbError,
                    $"too many active search sessions (limit {MaxSessions})", retryable: true));

            if (_sessions.TryRemove(victim.Value.Key, out var expired))
            {
                LpLog.Warn($"search snapshot session evicted for capacity: {victim.Value.Key}", category: "search.session");
                await DisposeSessionAsync(expired);
            }
        }
    }

    private void SweepExpired()
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var pair in _sessions)
            {
                if (now - pair.Value.LastAccess <= _ttl) continue;
                if (pair.Value.Gate.CurrentCount == 0) continue;   // 正在读页：Touch 后下一轮再判
                if (_sessions.TryRemove(pair.Key, out var expired))
                {
                    LpLog.Warn($"search snapshot session expired after {_ttl.TotalSeconds:F0}s: {pair.Key}",
                        category: "search.session");
                    _ = DisposeSessionAsync(expired);
                }
            }
        }
        catch (Exception ex)
        {
            LpLog.Warn("search snapshot session sweep failed", ex, category: "search.session");
        }

        if (_sessions.IsEmpty) StopSweepIfIdle();
        else ScheduleSweep();   // 自续期：只要还有会话，就继续按周期检查
    }

    private void ScheduleSweep()
    {
        try { _sweepTimer.Change(_sweepPeriod, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* 进程退出 */ }
    }

    private void StopSweepIfIdle()
    {
        if (!_sessions.IsEmpty) return;
        try { _sweepTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* 进程退出 */ }
    }

    private static async Task DisposeSessionAsync(Session session)
    {
        await session.Gate.WaitAsync();
        try
        {
            // ITransactionScope.DisposeAsync 在未提交/未回滚时负责回滚；读事务用它收尾。
            await session.Tx.DisposeAsync();
            await session.Uow.DisposeAsync();
        }
        catch (Exception ex)
        {
            // 关闭失败不能影响界面；但必须留痕（诊断面不允许静默）。
            LpLog.Warn("search snapshot session dispose failed", ex, category: "search.session");
        }
        finally
        {
            session.Gate.Release();
            // 不 Dispose 信号量：关闭方可能与刚取到会话的读页线程并发，Dispose 会让对方拿到
            // ObjectDisposedException 而不是可重试的“会话不存在”。让 GC 随会话一起回收更安全。
        }
    }

    private sealed class Session(
        LinkSearchScope scope, string sortBy, string sortOrder, IUnitOfWork uow, ITransactionScope tx,
        IReadOnlyList<FolderSearchHit> folders)
    {
        private long _lastAccessTicks = DateTimeOffset.UtcNow.UtcTicks;

        public LinkSearchScope Scope { get; } = scope;
        public string SortBy { get; } = sortBy;
        public string SortOrder { get; } = sortOrder;
        public IUnitOfWork Uow { get; } = uow;
        public ITransactionScope Tx { get; } = tx;

        /// <summary>本快照命中的文件夹行（已按当前排序键排好）；空 = 纯链接结果集，走原来的单表分页快路径。</summary>
        public IReadOnlyList<FolderSearchHit> Folders { get; } = folders;
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public DateTimeOffset LastAccess => new(Interlocked.Read(ref _lastAccessTicks), TimeSpan.Zero);
        public void Touch() => Interlocked.Exchange(ref _lastAccessTicks, DateTimeOffset.UtcNow.UtcTicks);
    }
}
