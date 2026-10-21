namespace TinyHarness.Core.Models.Session;

/// <summary>
///     <para>运行中接收的用户原话；未绑定 canonical 下标时尚未生效，不在重启时自动执行。</para>
///     <para>
///         User wording received during a run; without a canonical index it is unapplied and never auto-executed on
///         restart.
///     </para>
/// </summary>
public sealed record RunAmendment
{
    public          string         Schema                { get; init; } = "tinyharness.run-amendment/1";
    public required string         Id                    { get; init; }
    public required string         TurnId                { get; init; }
    public required string         RunId                 { get; init; }
    public required string         Text                  { get; init; }
    public required string         DisplayText           { get; init; }
    public          DateTimeOffset CreatedUtc            { get; init; } = DateTimeOffset.UtcNow;
    public          int?           CanonicalMessageIndex { get; init; }

    internal void Validate()
    {
        if (Schema != "tinyharness.run-amendment/1" ||
            !Guid.TryParseExact(Id, "N", out _) ||
            string.IsNullOrWhiteSpace(TurnId) ||
            string.IsNullOrWhiteSpace(RunId) ||
            string.IsNullOrWhiteSpace(Text) ||
            Text.Length > 16_384 ||
            DisplayText is null ||
            DisplayText.Length > 32_768 ||
            CanonicalMessageIndex is < 0)
            throw new InvalidDataException("Invalid run amendment record.");
    }
}

/// <summary>
///     <para>带来源的工作项陈述；Completed 仅表示来源方声称完成，不证明测试或副作用成功。</para>
///     <para>A sourced work-item claim; Completed means its author claims completion, not verified tests or side effects.</para>
/// </summary>
public sealed record SessionWorkItem
{
    public          string             Schema                 { get; init; } = "tinyharness.work-item/1";
    public required string             Id                     { get; init; }
    public required string             SourceTurnId           { get; init; }
    public required string             UpdatedRunId           { get; init; }
    public required string             Title                  { get; init; }
    public required string             DisplayTitle           { get; init; }
    public required string             Status                 { get; init; }
    public          string             Source                 { get; init; } = "model";
    public          int                Revision               { get; init; } = 1;
    public          IReadOnlyList<int> EvidenceMessageIndexes { get; init; } = [];
    public          DateTimeOffset     UpdatedUtc             { get; init; } = DateTimeOffset.UtcNow;

    internal void Validate()
    {
        if (Schema != "tinyharness.work-item/1" ||
            !Guid.TryParseExact(Id, "N", out _) ||
            string.IsNullOrWhiteSpace(SourceTurnId) ||
            string.IsNullOrWhiteSpace(UpdatedRunId) ||
            string.IsNullOrWhiteSpace(Title) ||
            Title.Length > 2048 ||
            DisplayTitle is null ||
            DisplayTitle.Length > 4096 ||
            Status is not ("pending" or "in_progress" or "completed" or "cancelled") ||
            Source is not ("user" or "model") ||
            Revision < 1 ||
            EvidenceMessageIndexes is null ||
            EvidenceMessageIndexes.Count > 32 ||
            EvidenceMessageIndexes.Any(index => index < 0) ||
            EvidenceMessageIndexes.Distinct().Count() != EvidenceMessageIndexes.Count)
            throw new InvalidDataException("Invalid session work-item record.");
    }
}

public sealed partial class ChatSession
{
    private readonly Dictionary<string, RunAmendment>    _amendments    = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionWorkItem> _workItems     = new(StringComparer.Ordinal);
    private readonly object                              _workStateGate = new();

    public IReadOnlyList<RunAmendment> Amendments
    {
        get
        {
            lock (_workStateGate)
            {
                return _amendments.Values.ToArray();
            }
        }
    }

    public IReadOnlyList<SessionWorkItem> WorkItems
    {
        get
        {
            lock (_workStateGate)
            {
                return _workItems.Values.ToArray();
            }
        }
    }

    internal void RecordAmendment(RunAmendment amendment)
    {
        amendment.Validate();
        lock (_workStateGate)
        {
            _amendments[amendment.Id] = amendment;
        }
    }

    internal void RecordWorkItem(SessionWorkItem item)
    {
        item.Validate();
        lock (_workStateGate)
        {
            _workItems[item.Id] = item;
        }
    }
}
