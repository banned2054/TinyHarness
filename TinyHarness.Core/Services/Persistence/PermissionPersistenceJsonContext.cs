using System.Text.Json.Serialization;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Permissions;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>
///         权限持久化记录的 JSON 源生成上下文：/2 审批模式选择与迁移记录。命名与枚举策略
///         与会话日志上下文一致；读取校验由记录自身的 Validate 提供，写入方使用本上下文。
///     </para>
///     <para>
///         The JSON source-generation context for permission persistence records: the /2 approval-mode selection
///         and the migration record. Naming and enum policies match the session-log context; record Validate
///         methods provide read-side checks, and writers use this context.
///     </para>
/// </summary>
[JsonSerializable(typeof(SessionPermissionSelectionV2))]
[JsonSerializable(typeof(LegacyPermissionSelection))]
[JsonSerializable(typeof(PermissionMigrationRecord))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             UseStringEnumConverter = true,
                             GenerationMode = JsonSourceGenerationMode.Metadata |
                                              JsonSourceGenerationMode.Serialization)]
public sealed partial class PermissionPersistenceJsonContext : JsonSerializerContext;
