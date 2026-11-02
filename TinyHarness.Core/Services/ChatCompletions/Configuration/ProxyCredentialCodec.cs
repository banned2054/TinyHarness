using System.Globalization;

namespace TinyHarness.Core.Services.ChatCompletions.Configuration;

/// <summary>
///     <para>把代理用户名和密码无歧义地编码为单个系统凭据条目；编码只用于安全存储，不用于 JSON。</para>
///     <para>
///         Encodes a proxy username and password unambiguously into one system credential entry; this encoding is only
///         for secure storage, never JSON.
///     </para>
/// </summary>
public static class ProxyCredentialCodec
{
    /// <summary>
    ///     <para>编码一对非空代理凭据。</para>
    ///     <para>Encodes a non-empty proxy credential pair.</para>
    /// </summary>
    public static string Encode(string username, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentException.ThrowIfNullOrEmpty(password);
        return username.Length.ToString(CultureInfo.InvariantCulture) + ":" + username + password;
    }

    /// <summary>
    ///     <para>解码凭据存储条目；格式损坏时不返回部分凭据。</para>
    ///     <para>Decodes a credential-store entry and never returns partial credentials when the stored format is invalid.</para>
    /// </summary>
    public static (string Username, string Password) Decode(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        var separator = value.IndexOf(':');
        if (separator <= 0 ||
            !int.TryParse(value.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture,
                          out var usernameLength) ||
            usernameLength <= 0                   || usernameLength >= value.Length - separator - 1)
            throw new InvalidDataException("The saved proxy credential entry is invalid.");

        var usernameStart = separator + 1;
        return (value.Substring(usernameStart, usernameLength), value[(usernameStart + usernameLength)..]);
    }
}
