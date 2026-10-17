namespace Dsh.Acp;

public static class AcpMethods
{
    public const int ProtocolVersion = 1;

    public const string Initialize = "initialize";
    public const string Authenticate = "authenticate";

    /** Auth method advertised by the local socket server: the client proves it runs as the same user by sending the token file's content in _meta.token. */
    public const string LocalTokenAuthMethod = "dsh-local-token";
    public const string NewSession = "session/new";
    public const string LoadSession = "session/load";
    public const string ListSessions = "session/list";
    public const string ResumeSession = "session/resume";
    public const string CloseSession = "session/close";
    public const string Prompt = "session/prompt";
    public const string Cancel = "session/cancel";

    public const string ClientSessionUpdate = "session/update";
    public const string ClientRequestPermission = "session/request_permission";

    public const string UpdateAgentMessageChunk = "agent_message_chunk";
    public const string UpdateAgentThoughtChunk = "agent_thought_chunk";
    public const string UpdateToolCall = "tool_call";
    public const string UpdateToolCallResult = "tool_call_update";

    public const string OutcomeSelected = "selected";
    public const string OutcomeCancelled = "cancelled";

    public const string StopEndTurn = "end_turn";
    public const string StopMaxTokens = "max_tokens";
    public const string StopMaxTurnRequests = "max_turn_requests";
    public const string StopRefusal = "refusal";
    public const string StopCancelled = "cancelled";

    public const string UpdateUserMessageChunk = "user_message_chunk";
    public const string UpdatePlan = "plan";
    public const string UpdateAvailableCommands = "available_commands_update";
    public const string UpdateCurrentMode = "current_mode_update";
    public const string UpdateConfigOption = "config_option_update";
    public const string UpdateSessionInfo = "session_info_update";
    public const string UpdateUsage = "usage_update";

    public const string PermissionAllowOnce = "allow_once";
    public const string PermissionAllowAlways = "allow_always";
    public const string PermissionRejectOnce = "reject_once";
    public const string PermissionRejectAlways = "reject_always";
}