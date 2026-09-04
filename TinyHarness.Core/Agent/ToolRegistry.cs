using TinyHarness.Core.Tools;

namespace TinyHarness.Core.Agent;

/// <summary>
/// Immutable registry of available tools. Tools are registered explicitly at the
/// composition root; no assembly scanning is performed.
/// </summary>
public sealed record ToolRegistry : IReadOnlyDictionary<string, ITool>
{
    private readonly IReadOnlyDictionary<string, ITool> _tools;

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        _tools = tools.ToDictionary(t => t.Definition.Name, t => t, StringComparer.Ordinal);
    }

    public IEnumerable<string> Keys => _tools.Keys;

    public IEnumerable<ITool> Values => _tools.Values;

    public int Count => _tools.Count;

    public bool ContainsKey(string key) => _tools.ContainsKey(key);

    public bool TryGetValue(string key, out ITool value) => _tools.TryGetValue(key, out value!);

    public ITool this[string key] => _tools[key];

    public IEnumerator<KeyValuePair<string, ITool>> GetEnumerator() => _tools.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
