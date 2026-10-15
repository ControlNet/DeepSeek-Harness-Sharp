#!/usr/bin/env python3
# Test-only ACP agent without any model: it treats every prompt as one shell command, asks the client for permission, and runs it for real when allowed.
# It speaks ACP schema v1 over stdio (one JSON-RPC message per line), so tests can drive the host's ACP path (events, plan, permission broadcast) deterministically.
import json
import subprocess
import sys
import uuid

PROTOCOL_VERSION = 1
METHOD_NOT_FOUND = -32601


def send(message):
    sys.stdout.write(json.dumps(message) + "\n")
    sys.stdout.flush()


def update(session_id, body):
    send({"jsonrpc": "2.0", "method": "session/update", "params": {"sessionId": session_id, "update": body}})


def say(session_id, text):
    update(session_id, {"sessionUpdate": "agent_message_chunk", "content": {"type": "text", "text": text}})


def plan(session_id, ran):
    entries = [
        {"content": "Ask for permission", "priority": "high", "status": "completed"},
        {"content": "Run the command", "priority": "high", "status": "completed" if ran else "in_progress"},
    ]
    update(session_id, {"sessionUpdate": "plan", "entries": entries})


class Agent:
    def __init__(self):
        self.cwd = {}
        # permission request id -> (prompt request id, session id, tool call id, command)
        self.waiting = {}

    def handle(self, message):
        method = message.get("method")
        if method is None:
            self.answered(message)
        elif method == "initialize":
            self.reply(message, {"protocolVersion": PROTOCOL_VERSION, "agentCapabilities": {"loadSession": False},
                                 "agentInfo": {"name": "scripted-acp-agent", "version": "1"}, "authMethods": []})
        elif method == "session/new":
            session_id = f"scripted-{uuid.uuid4().hex[:8]}"
            self.cwd[session_id] = message["params"].get("cwd")
            self.reply(message, {"sessionId": session_id})
        elif method == "session/prompt":
            self.prompt(message)
        elif method == "session/cancel":
            self.cancel(message["params"]["sessionId"])
        elif "id" in message:
            send({"jsonrpc": "2.0", "id": message["id"], "error": {"code": METHOD_NOT_FOUND, "message": f"method not found: {method}"}})

    def reply(self, request, result):
        send({"jsonrpc": "2.0", "id": request["id"], "result": result})

    def prompt(self, request):
        session_id = request["params"]["sessionId"]
        command = "".join(block.get("text", "") for block in request["params"]["prompt"]).strip()
        tool_call_id = f"call_{uuid.uuid4().hex[:8]}"
        say(session_id, f"I will run `{command}` once you allow it.")
        plan(session_id, ran=False)
        tool_call = {"toolCallId": tool_call_id, "title": command, "kind": "execute", "status": "pending"}
        update(session_id, {"sessionUpdate": "tool_call", **tool_call, "rawInput": {"command": command}})
        permission_id = f"perm-{uuid.uuid4().hex[:8]}"
        self.waiting[permission_id] = (request["id"], session_id, tool_call_id, command)
        send({"jsonrpc": "2.0", "id": permission_id, "method": "session/request_permission", "params": {
            "sessionId": session_id, "toolCall": tool_call,
            "options": [{"optionId": "allow", "name": "Allow", "kind": "allow_once"},
                        {"optionId": "reject", "name": "Reject", "kind": "reject_once"}]}})

    def answered(self, response):
        waiting = self.waiting.pop(response.get("id"), None)
        if waiting is None:
            return
        prompt_id, session_id, tool_call_id, command = waiting
        outcome = response.get("result", {}).get("outcome", {})
        if outcome.get("outcome") == "cancelled":
            send({"jsonrpc": "2.0", "id": prompt_id, "result": {"stopReason": "cancelled"}})
            return
        if outcome.get("optionId") == "allow":
            ran = subprocess.run(["/bin/sh", "-c", command], cwd=self.cwd.get(session_id), capture_output=True, text=True)
            status = "completed" if ran.returncode == 0 else "failed"
            text = (ran.stdout + ran.stderr).strip()
            plan(session_id, ran=True)
        else:
            status, text = "failed", "declined by the user"
        update(session_id, {"sessionUpdate": "tool_call_update", "toolCallId": tool_call_id, "status": status,
                            "content": [{"type": "content", "content": {"type": "text", "text": text}}]})
        say(session_id, f"Done: {text}")
        send({"jsonrpc": "2.0", "id": prompt_id, "result": {"stopReason": "end_turn"}})

    def cancel(self, session_id):
        for permission_id, (prompt_id, waiting_session, _, _) in list(self.waiting.items()):
            if waiting_session == session_id:
                del self.waiting[permission_id]
                send({"jsonrpc": "2.0", "id": prompt_id, "result": {"stopReason": "cancelled"}})


def main():
    agent = Agent()
    for line in sys.stdin:
        if line.strip():
            agent.handle(json.loads(line))


if __name__ == "__main__":
    main()
