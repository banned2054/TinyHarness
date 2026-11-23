using System.Text.Json;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         helper 拒读 ACL 簿记的共享并集实现：读取
///         <c>&lt;home&gt;\.sandbox\deny_read_acl_state.json</c>（fork windows-sandbox-rs
///         deny_read_state.rs 同源 schema，主体键为 SID），把全部主体的条目按配对规则过滤后
///         并入语义 deny 集。首次准备（<c>WindowsSandboxComposer</c> 顶层规划）与两个 refresh
///         点（<c>WindowsSandboxBackend.PreflightAsync</c>、<c>AuthorizedReadCoordinator</c>
///         的恢复/恢复 payload 构造）共用同一实现，不复制第二份。
///     </para>
///     <para>
///         The shared deny-read bookkeeping union implementation: reads
///         <c>&lt;home&gt;\.sandbox\deny_read_acl_state.json</c> (the same schema as fork
///         windows-sandbox-rs deny_read_state.rs, principal keys are SIDs) and folds every
///         principal's entries into the semantic deny set through the pairing filter. The first
///         preparation (the composer's top-level planning) and the two refresh points
///         (<c>WindowsSandboxBackend.PreflightAsync</c> and the
///         <c>AuthorizedReadCoordinator</c>'s restore-payload construction) share this one
///         implementation without a second copy.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         并集语义：全部主体的条目一律参与（跨主体多并入只会过保护，不做主体过滤）、只增
///         不减——helper 按主体全量同步（不在 payload 即撤销），任一端的 refresh 都不得撤销
///         共享主体与 home 的另一端正在依赖的扫描命中 deny；会话冻结期间另一端新写入簿记的
///         条目同样在下次 refresh 前被并入。配对规则与 Desktop 自身基线配对校验同口径：等于
///         某读根本身，或既不落在任何读根覆盖内也与读根非兄弟的条目丢弃（由写入端下次
///         refresh 自行恢复）；覆盖沙箱运行时路径的条目不丢弃——簿记条目本就是 helper 已对
///         某主体下发的 deny，重申不扩大暴露面，组合期配置错误仍由既有组合校验 fail-closed。
///     </para>
///     <para>
///         Union semantics: every principal's entries participate (merging across principals only
///         over-protects; no principal filtering) and the union is add-only — the helper syncs
///         wholesale per principal (revoking anything absent from the payload), so neither side's
///         refresh may revoke scan-hit denies the other side sharing the principal and home relies
///         on; entries another side writes into the bookkeeping while a session is frozen are folded
///         in before the next refresh too. The pairing rule matches the Desktop's own baseline
///         pairing validation: entries equal to a read root itself, or neither covered by any read
///         root nor a sibling of one, are dropped (the writing side restores them on its next
///         refresh); entries covering a sandbox runtime path are kept — a bookkeeping entry is a
///         deny the helper already applied to some principal, so restating it does not expand the
///         exposure, and composition-time configuration errors still fail closed at the existing
///         composition validation.
///     </para>
    ///     <para>
    ///         读取契约：可确认缺失（状态文件不存在，或父目录 .sandbox 不存在=全新 home 无 deny
    ///         历史）=空集贡献，不是错误；访问拒绝、共享冲突、其他 I/O 失败=瞬态类错误，经有界
    ///         重试（固定次数与短间隔）仍失败则 fail-closed，错误指明阶段与状态文件路径；JSON
    ///         损坏=立即 fail-closed 不重试——协调失败绝不静默收缩 deny。读取直接打开文件，不做
    ///         <c>File.Exists</c> 前置（该 API 对访问错误同样返回 false，会把不可读误判为缺失而
    ///         绕过重试与 fail-closed）。实现为同步读取与解析（与规划器既有同步枚举一致），重试
    ///         最坏情况阻塞两次短间隔。
    ///     </para>
    ///     <para>
    ///         Read contract: a confirmed absence (the state file does not exist, or the parent
    ///         .sandbox directory is missing — a fresh home has no deny history) contributes an
    ///         empty set and is not an error; access denied, sharing conflicts and other I/O
    ///         failures are transient-class errors that fail closed after a bounded retry (a fixed
    ///         attempt count with short intervals) with the stage and the state-file path named,
    ///         while corrupt JSON fails closed immediately without retrying — a failed coordination
    ///         must never silently shrink. The read opens the file directly without a
    ///         <c>File.Exists</c> guard (that API also returns false on access errors, which would
    ///         misreport an unreadable file as missing and bypass the retry and fail-closed paths).
    ///         The implementation reads and parses synchronously (matching the planner's existing
    ///         synchronous enumeration); the retry blocks for at most two short intervals in the
    ///         worst case.
    ///     </para>
/// </remarks>
internal static class SandboxDenyReadBookkeeping
{
    /// <summary>
    ///     <para>簿记状态文件固定位置：helper 按沙箱 home 写入 <c>&lt;home&gt;\.sandbox\deny_read_acl_state.json</c>。</para>
    ///     <para>
    ///         The fixed bookkeeping state-file location: the helper writes
    ///         <c>&lt;home&gt;\.sandbox\deny_read_acl_state.json</c> under the sandbox home.
    ///     </para>
    /// </summary>
    public static string StateFilePath(string sandboxHome) =>
        Path.Combine(sandboxHome, ".sandbox", "deny_read_acl_state.json");

    /// <summary>
    ///     <para>瞬态读取重试次数（含首次）；sharing violation 类 IO/权限错按短间隔重试。</para>
    ///     <para>
    ///         The transient-read retry attempt count (including the first); sharing-violation-class
    ///         I/O or permission errors retry after a short interval.
    ///     </para>
    /// </summary>
    private const int ReadAttemptCount = 3;

    /// <summary>
    ///     <para>瞬态读取重试的短间隔毫秒数；只覆盖并发读写簿记文件的毫秒级竞态。</para>
    ///     <para>
    ///         The short retry interval in milliseconds; it only covers millisecond-scale races
    ///         between concurrent readers and writers of the bookkeeping file.
    ///     </para>
    /// </summary>
    private const int ReadRetryDelayMilliseconds = 50;

    /// <summary>
    ///     <para>
    ///         合并 helper 拒读簿记：把全部主体的可配对条目并入语义 deny 集，返回合并结果与
    ///         计数（合并前条目总数、实际并入数）供诊断事件使用；诊断只记计数，绝不记录簿记
    ///         路径名。<paramref name="resolvedSandboxHome" /> 为 null（settings 缺失或解析失败）
    ///         跳过读取，直接返回语义集。
    ///     </para>
    ///     <para>
    ///         Merges the helper deny-read bookkeeping: pairable entries from every principal are
    ///         folded into the semantic deny set, returning the merged result with the counts (the
    ///         total entries before the merge and the entries actually merged) for the diagnostic
    ///         event; diagnostics record counts only, never bookkeeping path names. A null
    ///         <paramref name="resolvedSandboxHome" /> (missing settings or a resolution failure)
    ///         skips the read and returns the semantic set unchanged.
    ///     </para>
    /// </summary>
    /// <param name="failureStage">
    ///     <para>fail-closed 错误消息中的阶段名（如 <c>top-level baseline read-root planning</c>、<c>per-command refresh</c>）。</para>
    ///     <para>
    ///         The stage name inside the fail-closed error message (for example
    ///         <c>top-level baseline read-root planning</c> or <c>per-command refresh</c>).
    ///     </para>
    /// </param>
    /// <param name="semanticDenyPaths">
    ///     <para>语义 deny 集（冻结或本轮定型的集合，可能含待去重条目）。</para>
    ///     <para>The semantic deny set (frozen or finalized this round, possibly with entries pending dedup).</para>
    /// </param>
    /// <param name="readRoots">
    ///     <para>读根集合（payload 的 read_roots 通道或规划器定型读根），配对过滤的覆盖与兄弟判断依据。</para>
    ///     <para>
    ///         The read-root set (the payload's read_roots channel or the planner's finalized read
    ///         roots); the coverage and sibling basis of the pairing filter.
    ///     </para>
    /// </param>
    /// <param name="resolvedSandboxHome">
    ///     <para>解析成功的沙箱 home；null 跳过读取。</para>
    ///     <para>The resolved sandbox home; null skips the read.</para>
    /// </param>
    /// <param name="readStateContent">
    ///     <para>
    ///         簿记读取注入（输入=状态文件路径，输出=原始 JSON 内容，null=文件缺失，抛出=
    ///         fail-closed）；null 使用真实读取。测试注入同样经过瞬态重试。
    ///     </para>
    ///     <para>
    ///         The bookkeeping read injection (input = the state-file path, output = the raw JSON
    ///         content, null = file missing, throwing = fail closed); null uses the real read. Test
    ///         injections go through the transient retry as well.
    ///     </para>
    /// </param>
    /// <param name="cancellationToken"><para>取消令牌；取消原样抛出。</para><para>The cancellation token; cancellation propagates.</para></param>
    /// <returns>
    ///     <para>合并后的 deny 集（待最终去重）、簿记条目计数（合并前，含被过滤条目）与实际并入计数（合并后）。</para>
    ///     <para>
    ///         The merged deny set (pending final dedup) plus the bookkeeping entry counts (before
    ///         the merge, including filtered entries) and the entries actually merged (after).
    ///     </para>
    /// </returns>
    /// <exception cref="InvalidOperationException">
    ///     <para>
    ///         簿记不可读或损坏（有界重试后仍失败的 IO/权限错、JSON 解析错、条目无法绝对化）：
    ///         fail-closed，错误含阶段与状态文件路径，原异常保留为 inner exception。
    ///     </para>
    ///     <para>
    ///         The bookkeeping is unreadable or corrupt (I/O or permission errors still failing
    ///         after the bounded retry, JSON parse errors, an entry that cannot be absolutized):
    ///         fails closed with the stage and state-file path in the message and the original
    ///         exception kept as the inner exception.
    ///     </para>
    /// </exception>
    internal static (IReadOnlyList<string> DenyPaths, int BookkeepingEntries, int BookkeepingMerged) Merge(
        string                 failureStage,
        IReadOnlyList<string>  semanticDenyPaths,
        IReadOnlyList<string>  readRoots,
        string?                resolvedSandboxHome,
        Func<string, string?>? readStateContent = null,
        CancellationToken      cancellationToken = default)
    {
        if (resolvedSandboxHome is null) return (semanticDenyPaths, 0, 0);

        cancellationToken.ThrowIfCancellationRequested();
        var statePath = StateFilePath(resolvedSandboxHome);
        List<string> bookkeepingPaths;
        try
        {
            // 文件缺失=全新 home 无 deny 历史，不是错误；其余读取/解析失败一律 fail-closed。
            var content = ReadWithTransientRetry(statePath, readStateContent, cancellationToken);
            if (content is null) return (semanticDenyPaths, 0, 0);

            // 全部主体的条目一律并入：跨主体多并入只会过保护；空/空白条目无路径语义（真实簿记
            // 不可能产生，对应 ACL 也不存在），跳过；条目绝对化后参与配对过滤与合并。
            var state = JsonSerializer.Deserialize(content, WindowsSandboxJsonContext.Default.SandboxDenyReadAclState)
                        ?? throw new JsonException("The deny-read bookkeeping state is null.");
            bookkeepingPaths = state.Principals.Values
                                     .SelectMany(entries => entries ?? [])
                                     .Where(entry => !string.IsNullOrWhiteSpace(entry))
                                     .Select(Path.GetFullPath)
                                     .ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException
                                     or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException($"The {failureStage} could not read the helper deny-read bookkeeping at '{statePath}': {ex.Message}; " +
                                                  "the deny set must never silently shrink entries another sandbox principal relies on, " +
                                                  "so the operation fails closed. Repair or remove the bookkeeping file to proceed.", ex);
        }

        var fullReadRoots = readRoots.Select(Path.GetFullPath).ToArray();
        var merged        = new List<string>(semanticDenyPaths);
        foreach (var entry in bookkeepingPaths)
            if (IsPairableBaselineDeny(entry, fullReadRoots))
                merged.Add(entry);

        return (merged, bookkeepingPaths.Count, merged.Count - semanticDenyPaths.Count);
    }

    /// <summary>
    ///     <para>
    ///         读取簿记内容并对 IO/权限类瞬态失败做有界重试；文件缺失返回 null（默认实现）。
    ///         重试只覆盖并发读写簿记文件的毫秒级竞态，解析错不重试。
    ///     </para>
    ///     <para>
    ///         Reads the bookkeeping content with a bounded retry for transient I/O or permission
    ///         failures; a missing file returns null (the default implementation). The retry only
    ///         covers millisecond-scale races between concurrent readers and writers of the
    ///         bookkeeping file; parse errors never retry.
    ///     </para>
    /// </summary>
    private static string? ReadWithTransientRetry(
        string statePath, Func<string, string?>? readStateContent, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return (readStateContent ?? ReadDenyReadAclStateContent)(statePath);
            }
            catch (Exception ex) when (attempt < ReadAttemptCount &&
                                       ex is IOException or UnauthorizedAccessException)
            {
                // 注入委托抛出的 FileNotFoundException 等 IOException 子类按注入契约（抛出=
                // fail-closed）同样重试后 fail-closed；默认实现已在内部把可确认缺失转为 null。
                cancellationToken.ThrowIfCancellationRequested();
                Thread.Sleep(ReadRetryDelayMilliseconds);
            }
        }
    }

    /// <summary>
    ///     <para>
    ///         读取 helper 拒读 ACL 簿记状态文件的原始内容（默认读取实现）：直接打开读取，不做
    ///         <c>File.Exists</c> 前置——该 API 对访问拒绝等错误同样返回 false，会把不可读误判为
    ///         缺失而绕过瞬态重试与 fail-closed。仅 FileNotFoundException（文件确认缺失）与
    ///         DirectoryNotFoundException（父目录 .sandbox 不存在，文件不可能存在）返回 null，即
    ///         空集贡献（全新 home 冷启动形态）；访问拒绝、共享冲突、其他 IO 失败如实抛出，由
    ///         瞬态重试与 fail-closed 链路处理。同步读取与规划器既有的同步枚举/属性检查一致。
    ///     </para>
    ///     <para>
    ///         Reads the raw content of the helper deny-read ACL bookkeeping state file (the
    ///         default read implementation): opens and reads directly without a <c>File.Exists</c>
    ///         guard — that API also returns false on access-denied and similar errors, which would
    ///         misreport an unreadable file as missing and bypass the transient retry and fail-closed
    ///         paths. Only FileNotFoundException (the file is confirmed missing) and
    ///         DirectoryNotFoundException (the parent .sandbox directory does not exist, so the file
    ///         cannot) return null, an empty-set contribution (the fresh-home cold-start shape);
    ///         access denied, sharing conflicts and other I/O failures propagate as-is into the
    ///         transient retry and fail-closed chain. The synchronous read matches the planner's
    ///         existing synchronous enumeration and attribute checks.
    ///     </para>
    /// </summary>
    private static string? ReadDenyReadAclStateContent(string stateFilePath)
    {
        try
        {
            return File.ReadAllText(stateFilePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // 仅这两种异常可确认缺失：文件不存在，或父目录（.sandbox）不存在=全新 home 无 deny
            // 历史；访问拒绝、共享冲突、其他 IO 失败如实抛出，由瞬态重试与 fail-closed 链路处理。
            return null;
        }
    }

    /// <summary>
    ///     <para>
    ///         判断簿记并入条目是否满足 Desktop 基线 deny 的配对校验（与组合兜底校验同口径）：
    ///         等于某读根本身，或既不落在任何读根覆盖内也与读根非兄弟 → 丢弃；覆盖沙箱运行时
    ///         路径的条目不在此过滤（见类型 remarks 的并集语义）。
    ///     </para>
    ///     <para>
    ///         Decides whether a bookkeeping entry satisfies the Desktop's baseline deny pairing
    ///         validation (the same criteria as the composition guard): an entry equal to a read
    ///         root itself, or neither covered by any read root nor a sibling of one, is dropped;
    ///         entries covering a sandbox runtime path are not filtered here (see the union
    ///         semantics in the type remarks).
    ///     </para>
    /// </summary>
    private static bool IsPairableBaselineDeny(string entryFull, IReadOnlyList<string> fullReadRoots)
    {
        // 等于某读根本身的条目丢弃：deny 读根会让组合校验拒绝整个基线。
        if (fullReadRoots.Any(readRoot => string.Equals(readRoot, entryFull, StringComparison.OrdinalIgnoreCase)))
            return false;

        // 覆盖内或兄弟（已被排除出读根的顶层项形态）二者其一即可并入。
        return fullReadRoots.Any(readRoot => Workspace.IsInside(readRoot, entryFull)) ||
               fullReadRoots.Any(readRoot =>
                                         string.Equals(Path.GetDirectoryName(readRoot),
                                                       Path.GetDirectoryName(entryFull),
                                                       StringComparison.OrdinalIgnoreCase));
    }
}
