using System.Text.Json.Serialization;

namespace Kairo.Protocol;

/// <summary>Source-generated serializers for every protocol type, so the core stays Native AOT compatible.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(RpcRequest))]
[JsonSerializable(typeof(RpcResponse))]
[JsonSerializable(typeof(RpcNotification))]
[JsonSerializable(typeof(AppInfo))]
[JsonSerializable(typeof(Empty))]
[JsonSerializable(typeof(Ok))]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(SettingsUpdate))]
[JsonSerializable(typeof(SecretSetParams))]
[JsonSerializable(typeof(WorkspacePickResult))]
[JsonSerializable(typeof(WorkspaceListResult))]
[JsonSerializable(typeof(SessionListResult))]
[JsonSerializable(typeof(SessionCreateParams))]
[JsonSerializable(typeof(SessionSummary))]
[JsonSerializable(typeof(SessionRef))]
[JsonSerializable(typeof(SessionDetail))]
[JsonSerializable(typeof(SessionSendParams))]
[JsonSerializable(typeof(SessionModeParams))]
[JsonSerializable(typeof(PermissionRespondParams))]
[JsonSerializable(typeof(AgentEventDto))]
[JsonSerializable(typeof(TerminalOpenParams))]
[JsonSerializable(typeof(TerminalInfo))]
[JsonSerializable(typeof(TerminalInputParams))]
[JsonSerializable(typeof(TerminalResizeParams))]
[JsonSerializable(typeof(TerminalRef))]
[JsonSerializable(typeof(TerminalOutput))]
[JsonSerializable(typeof(TerminalExited))]
[JsonSerializable(typeof(UpdateStatus))]
[JsonSerializable(typeof(McpListResult))]
[JsonSerializable(typeof(McpAddParams))]
[JsonSerializable(typeof(McpNameParams))]
[JsonSerializable(typeof(McpEnabledParams))]
public sealed partial class ProtocolJsonContext : JsonSerializerContext;
