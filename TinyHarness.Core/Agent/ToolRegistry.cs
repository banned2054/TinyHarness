using TinyHarness.Core.Tools;

namespace TinyHarness.Core.Agent;

/// <summary>
/// 不可变的工具注册表。工具由组合根显式注册，不进行程序集扫描，以保持流程清晰且兼容 NativeAOT。
///
/// Immutable registry of available tools. Tools are registered explicitly at the
/// composition root; no assembly scanning is performed.
/// </summary>
public sealed record ToolRegistry : IReadOnlyDictionary<string, ITool>
{
    private readonly IReadOnlyDictionary<string, ITool> _tools;

    /// <summary>
    /// 按工具名建立区分大小写的索引；重复名称会立即抛出异常。
    /// Builds an ordinal name index; duplicate tool names fail immediately.
    /// </summary>
    public ToolRegistry(IEnumerable<ITool> tools)
    {
        _tools = tools.ToDictionary(t => t.Definition.Name, t => t, StringComparer.Ordinal);
    }

    public IEnumerable<string> Keys => _tools.Keys;

    public IEnumerable<ITool> Values => _tools.Values;

    public int Count => _tools.Count;

    /// <summary>
    /// 判断是否注册了指定名称的工具。
    /// Reports whether a tool with the specified name is registered.
    /// </summary>
    public bool ContainsKey(string key) => _tools.ContainsKey(key);

    /// <summary>
    /// 尝试按名称取得工具，不存在时返回 <see langword="false"/>。
    /// Attempts to retrieve a tool by name and returns <see langword="false"/> when absent.
    /// </summary>
    public bool TryGetValue(string key, out ITool value) => _tools.TryGetValue(key, out value!);

    public ITool this[string key] => _tools[key];

    /// <summary>
    /// 按底层只读字典枚举已注册工具。
    /// Enumerates registered tools through the underlying read-only dictionary.
    /// </summary>
    public IEnumerator<KeyValuePair<string, ITool>> GetEnumerator() => _tools.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
