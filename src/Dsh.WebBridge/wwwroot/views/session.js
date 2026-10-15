// One session: its header and actions, its pending decisions, and either the message transcript with a composer or the terminal.
import { h, mount, link, ago, when, toast } from '../dom.js';
import { Capability, SessionKind, SessionStatus, describe, isTerminalSession } from '../hub.js';
import { sessionRoute, sessionTitle, statusChip, machineChip, filesRoute, gitRoute, emptyState } from './common.js';
import { syncPendingCards } from './pending.js';
import { Transcript } from './transcript.js';
import { terminalPanel } from './terminal.js';

export function sessionView(container, hub, { machine, sessionId }) {
  const machineLink = hub.link(machine);
  if (machineLink === undefined) {
    mount(container, emptyState(`No machine named ${machine}.`), link('/', {}, 'Back to the dashboard'));
    return {};
  }

  const head = h('header', { className: 'session-head' });
  const pending = h('div', { className: 'pending-list session-pending' });
  const body = h('div', { className: 'session-body' });
  mount(container, head, pending, body);
  let content = null;

  function update() {
    const session = machineLink.sessions.get(sessionId);
    renderHead(head, machineLink, sessionId, session);
    syncPendingCards(pending, hub, hub.pending().filter((item) => item.machine === machine && item.request.SessionId === sessionId));
    if (content === null && session !== undefined) {
      content = isTerminalSession(session) ? terminalPanel(machineLink, sessionId) : conversation(machineLink, session);
      mount(body, content.element);
      content.start?.();
    } else if (content === null)
      mount(body, emptyState(machineLink.connected ? 'This session is not known to the host.' : `Waiting for ${machine} to connect…`));
    content?.update?.(session);
  }

  update();
  return { update, dispose: () => content?.dispose?.() };
}

function renderHead(head, machineLink, sessionId, session) {
  const title = session ? sessionTitle(session) : sessionId;
  mount(head,
    h('div', { className: 'session-title-row' },
      link('/', { className: 'back', 'aria-label': 'Back to the dashboard' }, '←'),
      h('h1', { title: sessionId }, title),
      session ? statusChip(session.Status) : null),
    h('p', { className: 'muted session-facts' },
      machineLink.name, ' ', machineChip(machineLink),
      session ? ` · ${[session.AgentKind, session.Kind].filter(Boolean).join(' · ')}` : '',
      session?.ProjectPath ? ` · ${session.ProjectPath}` : '',
      session ? ' · ' : '',
      session ? h('time', { title: when(session.LastActivityAt) }, ago(session.LastActivityAt)) : null),
    session ? h('div', { className: 'actions session-actions' }, sessionActions(machineLink, session)) : null);
}

function sessionActions(machineLink, session) {
  const exited = session.Status === SessionStatus.Exited;
  const agentApi = machineLink.supports(Capability.AgentSessions);
  return [
    exited && agentApi ? actionButton('Resume', 'primary', () => resume(machineLink, session)) : null,
    !exited ? actionButton('Stop', 'danger', () => stop(machineLink, session)) : null,
    session.ProjectPath ? link(filesRoute(machineLink.name, session.ProjectPath), { className: 'button' }, 'Files') : null,
    session.ProjectPath && machineLink.supports(Capability.GitInspect) ? link(gitRoute(machineLink.name, session.ProjectPath), { className: 'button' }, 'Git') : null,
  ];
}

function actionButton(label, tone, action) {
  const button = h('button', {
    type: 'button',
    className: tone,
    onClick: async () => {
      button.disabled = true;
      try {
        await action();
      } catch (error) {
        toast(`${label} failed: ${describe(error)}`, 'error');
      } finally {
        button.disabled = false;
      }
    },
  }, label);
  return button;
}

async function stop(machineLink, session) {
  if (!confirm(`Stop ${sessionTitle(session)} on ${machineLink.name}?`))
    return;
  if (machineLink.supports(Capability.AgentSessions))
    await machineLink.request('host.agent.stop', { SessionId: session.SessionId });
  else if (isTerminalSession(session))
    await machineLink.request('host.pty.stop', { PtyId: session.SessionId });
  else
    await machineLink.request('host.session.interrupt', { SessionId: session.SessionId });
}

/** Terminal-mode agents resume as a new session (a new id); the view follows it. */
async function resume(machineLink, session) {
  const resumed = await machineLink.request('host.agent.resume', { SessionId: session.SessionId });
  if (resumed.SessionId !== session.SessionId)
    location.hash = `#${sessionRoute(machineLink.name, resumed.SessionId)}`;
}

/** Transcript plus composer for harness and ACP sessions. */
function conversation(machineLink, session) {
  const transcript = new Transcript();
  const text = h('textarea', { id: 'composer-text', rows: 2, placeholder: 'Message the agent (Ctrl+Enter to send)', 'aria-label': 'Message' });
  const sendButton = h('button', { type: 'submit', className: 'primary', id: 'composer-send' }, 'Send');
  const cancelButton = h('button', { type: 'button', id: 'composer-cancel', onClick: () => cancel() }, 'Cancel turn');
  const form = h('form', { className: 'composer', onSubmit: (event) => send(event) }, text, h('div', { className: 'actions' }, cancelButton, sendButton));
  const agentApi = machineLink.supports(Capability.AgentSessions);

  text.addEventListener('keydown', (event) => {
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey))
      form.requestSubmit();
  });

  async function send(event) {
    event.preventDefault();
    const message = text.value.trim();
    if (message.length === 0)
      return;
    sendButton.disabled = true;
    try {
      await (agentApi
        ? machineLink.request('host.agent.prompt', { SessionId: session.SessionId, Text: message })
        : machineLink.request('host.session.message', { SessionId: session.SessionId, Text: message }));
      text.value = '';
    } catch (error) {
      toast(`Not sent: ${describe(error)}`, 'error');
    } finally {
      sendButton.disabled = false;
    }
  }

  async function cancel() {
    try {
      await (agentApi
        ? machineLink.request('host.agent.cancel', { SessionId: session.SessionId })
        : machineLink.request('host.session.interrupt', { SessionId: session.SessionId }));
    } catch (error) {
      toast(`Cancel failed: ${describe(error)}`, 'error');
    }
  }

  const unsubscribe = machineLink.subscribe(session.SessionId, {
    event: (event) => transcript.apply(event),
    error: (message) => transcript.showError(`Cannot follow this session: ${message}`),
  });

  return {
    element: h('div', { className: 'conversation' }, transcript.element, form),
    update(current) {
      const exited = current?.Status === SessionStatus.Exited;
      text.disabled = sendButton.disabled = exited;
      cancelButton.hidden = current?.Status !== SessionStatus.Running || current?.Kind === SessionKind.Terminal;
    },
    dispose: unsubscribe,
  };
}
