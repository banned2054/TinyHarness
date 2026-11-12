namespace TinyHarness.Core.Models.Configuration;

/// <summary>
///     <para>自动命名的独立模型设置；不继承或修改 AI 审批设置。</para>
///     <para>Independent model settings for automatic titles; never inherits or changes AI approval settings.</para>
/// </summary>
public sealed record SessionTitleSettings
{
    public bool    UseCurrentModel { get; init; } = true;
    public string? Profile         { get; init; }
    public string? Model           { get; init; }

    /// <summary>
    ///     <para>指定模型时要求同时提供供应商和模型。</para>
    ///     <para>Requires both provider and model when using a fixed model.</para>
    /// </summary>
    public void Validate()
    {
        if (!UseCurrentModel && (string.IsNullOrWhiteSpace(Profile) || string.IsNullOrWhiteSpace(Model)))
            throw new InvalidDataException("Select a provider and model for automatic session titles.");
    }
}
