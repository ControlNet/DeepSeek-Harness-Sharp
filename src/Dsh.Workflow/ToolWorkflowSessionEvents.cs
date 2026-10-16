using Dsh.Plugins;
using System.Runtime.CompilerServices;
using Dsh.Core;

namespace Dsh.Workflow;

internal static class ToolWorkflowCodecRegistration
{
    [DshPluginInitializer]
    internal static void Register()
    {
        SessionEventCodec.Register<ToolWorkflowRunStartPayload>(ToolWorkflowRunStartPayload.EventType);
        SessionEventCodec.Register<ToolWorkflowAgentStartPayload>(ToolWorkflowAgentStartPayload.EventType);
        SessionEventCodec.Register<ToolWorkflowAgentEndPayload>(ToolWorkflowAgentEndPayload.EventType);
        SessionEventCodec.Register<ToolWorkflowRunEndPayload>(ToolWorkflowRunEndPayload.EventType);
        SessionEventRelations.Register<ToolWorkflowRunStartPayload>(
            ToolWorkflowRunStartPayload.EventType,
            payload => [new SessionEventRelation.Opens(Run(payload.RunId))]);
        SessionEventRelations.Register<ToolWorkflowRunEndPayload>(
            ToolWorkflowRunEndPayload.EventType,
            payload => [new SessionEventRelation.RefersTo(Run(payload.RunId), SessionEventEdgeKind.Completes)]);
        SessionEventRelations.Register<ToolWorkflowAgentStartPayload>(
            ToolWorkflowAgentStartPayload.EventType,
            payload => [new SessionEventRelation.Opens(Agent(payload.RunId, payload.Seq)), new SessionEventRelation.Spawns(payload.ChildId)]);
        SessionEventRelations.Register<ToolWorkflowAgentEndPayload>(
            ToolWorkflowAgentEndPayload.EventType,
            payload => [new SessionEventRelation.RefersTo(Agent(payload.RunId, payload.Seq), SessionEventEdgeKind.Completes)]);
    }

    private static SessionEventCorrelation Run(WorkflowRunId runId) => new(ToolWorkflowRunStartPayload.EventType, runId.Value);

    private static SessionEventCorrelation Agent(WorkflowRunId runId, int seq)
        => new(ToolWorkflowAgentStartPayload.EventType, $"{runId.Value}/{seq}");
}