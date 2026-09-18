namespace TinyHarness.Core.Runtime;

/// <summary>
/// 系统凭据存储的最小抽象：按目标名称保存、读取和删除密钥。实现绝不把密钥值写入日志或异常。
///
/// Minimal abstraction over a system credential store: save, read, and delete secrets by target name.
/// Implementations never write secret values to logs or exceptions.
/// </summary>
public interface ICredentialStore
{
    /// <summary>
    /// 当前平台是否支持该凭据存储。
    /// Whether this credential store is supported on the current platform.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// 保存或覆盖一个凭据条目。
    /// Saves or overwrites one credential entry.
    /// </summary>
    void Save(string targetName, string secret);

    /// <summary>
    /// 读取一个凭据条目；不存在时返回 <see langword="null"/>。
    /// Reads one credential entry; <see langword="null"/> when it does not exist.
    /// </summary>
    string? Read(string targetName);

    /// <summary>
    /// 删除一个凭据条目；返回是否存在并被删除。
    /// Deletes one credential entry; returns whether it existed and was deleted.
    /// </summary>
    bool Delete(string targetName);
}
