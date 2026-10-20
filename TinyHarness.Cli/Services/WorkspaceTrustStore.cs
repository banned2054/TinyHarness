using System.Text;
using System.Text.Json.Nodes;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Cli.Services;

/// <summary>
///     <para>在工作区及附加写根之外原子保存目录信任；权限就绪状态不持久化。</para>
///     <para>Atomically stores directory trust outside workspace and additional write roots; readiness is never persisted.</para>
/// </summary>
internal sealed class WorkspaceTrustStore(string filePath, IReadOnlyList<string> writeRoots)
{
    private const int MaximumBytes = 1024 * 1024;

    /// <summary>
    ///     <para>读取精确路径和目录身份的决定，不继承父目录信任。</para>
    ///     <para>Reads a decision for the exact path and directory identity without inheriting parent trust.</para>
    /// </summary>
    public async Task<bool?> ReadAsync(WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken)
    {
        var root = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (root["workspaces"] is not JsonObject entries) return null;
        return entries[Key(identity)] is JsonObject entry &&
               entry["fileIdentity"]?.GetValue<string>() == identity.FileIdentity
            ? entry["trusted"]?.GetValue<bool>()
            : null;
    }

    /// <summary>
    ///     <para>串行合并最新记录后原子替换文件；失败不提升当前权限。</para>
    ///     <para>Serializes and merges current records before atomic replacement; failure never increases access.</para>
    /// </summary>
    public async Task SaveAsync(WorkspaceDirectoryIdentity identity, bool trusted, CancellationToken cancellationToken)
    {
        ValidateLocation();
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        Directory.CreateDirectory(directory);
        ValidateLocation();
        await using var gate = new FileStream(filePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
                                              FileShare.None, 1, FileOptions.Asynchronous);
        identity.EnsureUnchanged();
        var root = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (root["workspaces"] is not JsonObject) root["workspaces"] = new JsonObject();
        var entries = (JsonObject)root["workspaces"]!;
        entries[Key(identity)] = new JsonObject
        {
            ["fileIdentity"] = identity.FileIdentity,
            ["trusted"]      = trusted
        };
        var json = root.ToJsonString();
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes)
            throw new InvalidDataException("Workspace trust store exceeds its size limit.");
        var temporary = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, json, cancellationToken).ConfigureAwait(false);
            identity.EnsureUnchanged();
            File.Move(temporary, filePath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task<JsonObject> LoadAsync(CancellationToken cancellationToken)
    {
        ValidateLocation();
        if (!File.Exists(filePath)) return new JsonObject { ["version"] = 1 };
        if (new FileInfo(filePath).Length > MaximumBytes)
            throw new InvalidDataException("Workspace trust store exceeds its size limit.");
        var json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        var root = JsonNode.Parse(json) as JsonObject;
        if (root?["version"]?.GetValue<int>() != 1 || root["workspaces"] is not JsonObject)
            throw new InvalidDataException("Invalid workspace trust store; refusing to overwrite it.");
        return root;
    }

    private void ValidateLocation()
    {
        foreach (var writeRoot in writeRoots)
        {
            SessionStorePaths.ResolveProtected(Path.GetDirectoryName(Path.GetFullPath(filePath)), writeRoot);
            if (File.Exists(filePath) && new FileInfo(filePath).LinkTarget is not null)
                throw new InvalidDataException("Workspace trust store may not be a link.");
            if (File.Exists(filePath + ".lock") && new FileInfo(filePath + ".lock").LinkTarget is not null)
                throw new InvalidDataException("Workspace trust lock may not be a link.");
        }
    }

    private static string Key(WorkspaceDirectoryIdentity identity) => OperatingSystem.IsWindows()
        ? identity.Path.ToUpperInvariant()
        : identity.Path;
}
