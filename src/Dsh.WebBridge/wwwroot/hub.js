// Client-side model of every machine the bridge serves.
// Each machine gets its own WebSocket (the bridge relays it to that machine's host), reconnecting with exponential backoff.
// After every (re)connection the link says host.hello, reloads sessions, pending decisions and notifications, then resumes event subscriptions from the next unseen seq and re-attaches terminals.
import { HostRpc, bridgeUrl } from './rpc.js';
import { base64ToBytes, textToBase64 } from './dom.js';

/** Must equal Dsh.RemoteHost HostProtocol.Version; host.hello reports a mismatch otherwise. */
export const PROTOCOL_VERSION = 1;

/** Shown by hosts as who answered an approval or question. */
const CLIENT_NAME = 'web';

const INITIAL_BACKOFF_MS = 500;
const MAX_BACKOFF_MS = 30000;

/** How often the bridge's machine list (connection states, deployment progress) is re-read. */
const MACHINE_POLL_MS = 5000;

/** Notifications loaded per machine; newer ones arrive as pushes. */
const NOTIFY_PAGE = 50;

/** Resolved request ids remembered per machine, so a pending list read just before a resolution cannot bring the request back. */
const RESOLVED_MEMORY = 1024;

export const Capability = {
  PendingBroadcast: 'pending.broadcast',
  SessionRegistry: 'sessions.registry',
  PtyResize: 'pty.resize',
  AgentSessions: 'agent.sessions',
  GitInspect: 'git.inspect',
  FsTransfer: 'fs.transfer',
  NotifyHistory: 'notify.history',
};

export const SessionStatus = { Running: 'Running', WaitingForUser: 'WaitingForUser', Idle: 'Idle', Exited: 'Exited' };

export const SessionKind = { Harness: 'harness', External: 'external', Acp: 'acp', Terminal: 'terminal' };

/** Sessions shown as a terminal (PTY) rather than as a message transcript. */
export function isTerminalSession(session) {
  return session.Kind === SessionKind.Terminal || session.Kind === SessionKind.External;
}

/** A session needs attention while it waits for the user or has unanswered approvals or questions. */
export function needsAttention(session) {
  return session.Status === SessionStatus.WaitingForUser || session.PendingCount > 0;
}

export class Hub extends EventTarget {
  /** Machine name to MachineLink, in the bridge's order. */
  links = new Map();

  /** The bridge's view of each machine: Machine, Kind, Target, State, HostVersion, Detail. */
  machines = [];

  #changeQueued = false;

  async start() {
    await this.refreshMachines();
    setInterval(() => this.refreshMachines().catch(() => {}), MACHINE_POLL_MS);
    const retryAll = () => {
      for (const link of this.links.values())
        link.retryNow();
    };
    window.addEventListener('online', retryAll);
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'visible')
        retryAll();
    });
  }

  async refreshMachines() {
    const response = await fetch('/api/machines', { credentials: 'same-origin', cache: 'no-store' });
    if (response.status === 401) {
      this.dispatchEvent(new Event('unauthorized'));
      return;
    }
    if (!response.ok)
      throw new Error(`machine list failed (${response.status})`);
    this.machines = await response.json();
    for (const status of this.machines) {
      let link = this.links.get(status.Machine);
      if (link === undefined) {
        link = new MachineLink(status.Machine, this);
        this.links.set(status.Machine, link);
        link.start();
      }
      link.bridgeStatus = status;
      if (status.State === 'Connected')
        link.retryNow();
    }
    this.changed();
  }

  /** Coalesces many pushes into one 'change' event per task. */
  changed() {
    if (this.#changeQueued)
      return;
    this.#changeQueued = true;
    queueMicrotask(() => {
      this.#changeQueued = false;
      this.dispatchEvent(new Event('change'));
    });
  }

  link(machine) {
    return this.links.get(machine);
  }

  /** Every known session with its machine, in machine order. */
  sessions() {
    return [...this.links.values()].flatMap((link) => [...link.sessions.values()].map((session) => ({ machine: link.name, session })));
  }

  /** Every unanswered approval and question with its machine. */
  pending() {
    return [...this.links.values()].flatMap((link) => [
      ...[...link.approvals.values()].map((request) => ({ machine: link.name, kind: 'approval', request })),
      ...[...link.questions.values()].map((request) => ({ machine: link.name, kind: 'question', request })),
    ]);
  }

  /** Notifications of every machine, newest first. */
  notifications() {
    return [...this.links.values()]
      .flatMap((link) => link.notifications.map((record) => ({ machine: link.name, record })))
      .sort((left, right) => Date.parse(right.record.CreatedAt) - Date.parse(left.record.CreatedAt));
  }

  unreadCount() {
    return [...this.links.values()].reduce((sum, link) => sum + link.unread, 0);
  }

  /** Machines the page cannot currently talk to. */
  offlineCount() {
    return [...this.links.values()].filter((link) => !link.connected).length;
  }
}

/** One machine: its WebSocket, its sessions, pending decisions and notifications, and the subscriptions views hold on it. */
export class MachineLink {
  state = 'connecting';
  error = '';
  hello = null;
  info = null;
  bridgeStatus = null;
  sessions = new Map();
  approvals = new Map();
  questions = new Map();
  notifications = [];
  unread = 0;

  #hub;
  #rpc = null;
  #generation = 0;
  #failures = 0;
  #retryTimer = null;
  #everConnected = false;
  #resolved = new Set();
  #subscriptions = new Map();
  #terminals = new Map();

  constructor(name, hub) {
    this.name = name;
    this.#hub = hub;
  }

  get connected() {
    return this.state === 'connected';
  }

  supports(capability) {
    return (this.hello?.Capabilities ?? []).includes(capability);
  }

  start() {
    this.#connect();
  }

  /** Skips the remaining backoff when the link is waiting to reconnect. */
  retryNow() {
    if (this.state !== 'waiting')
      return;
    clearTimeout(this.#retryTimer);
    this.#connect();
  }

  /** A request on this machine's connection; fails at once while it is not connected. */
  request(method, params) {
    if (this.#rpc === null || !this.connected)
      return Promise.reject(new Error(`${this.name} is not connected`));
    return this.#rpc.request(method, params);
  }

  /**
   * Follows a session's events across reconnects; listener.event gets each event once in seq order, listener.error a failure text.
   * A second listener of the same session first gets the events delivered so far.
   * Returns the function that stops following.
   */
  subscribe(sessionId, listener) {
    let subscription = this.#subscriptions.get(sessionId);
    if (subscription === undefined) {
      subscription = { listeners: new Set(), next: 0, log: [] };
      this.#subscriptions.set(sessionId, subscription);
    }
    subscription.listeners.add(listener);
    if (subscription.listeners.size === 1)
      this.#subscribeOnHost(sessionId, subscription);
    else
      subscription.log.forEach((event) => listener.event(event));
    return () => {
      subscription.listeners.delete(listener);
      if (subscription.listeners.size > 0)
        return;
      this.#subscriptions.delete(sessionId);
      this.request('host.session.unsubscribe', { SessionId: sessionId }).catch(() => {});
    };
  }

  /**
   * Shows a PTY across reconnects; listener.data(bytes, isSnapshot) gets output, where the first output of every attach is a full-screen snapshot.
   * listener.eof() runs when the PTY ends, listener.error(text) when attaching fails.
   */
  attachTerminal(ptyId, listener) {
    let terminal = this.#terminals.get(ptyId);
    if (terminal === undefined) {
      terminal = { listeners: new Set(), snapshotNext: true };
      this.#terminals.set(ptyId, terminal);
    }
    terminal.listeners.add(listener);
    if (terminal.listeners.size === 1)
      this.#attachOnHost(ptyId, terminal);
    return () => {
      terminal.listeners.delete(listener);
      if (terminal.listeners.size > 0)
        return;
      this.#terminals.delete(ptyId);
      this.request('host.pty.detach', { PtyId: ptyId }).catch(() => {});
    };
  }

  writeTerminal(ptyId, text) {
    return this.request('host.pty.write', { PtyId: ptyId, Base64: textToBase64(text) });
  }

  resizeTerminal(ptyId, columns, rows) {
    if (!this.supports(Capability.PtyResize))
      return Promise.resolve(false);
    return this.request('host.pty.resize', { PtyId: ptyId, Columns: columns, Rows: rows });
  }

  async markRead(ids) {
    return this.request('host.notify.markRead', ids === undefined ? { All: true } : { Ids: ids });
  }

  async #connect() {
    const generation = ++this.#generation;
    this.#retryTimer = null;
    this.state = this.#everConnected ? 'reconnecting' : 'connecting';
    this.#hub.changed();
    let rpc = null;
    try {
      rpc = await HostRpc.connect(bridgeUrl(this.name));
      this.#wire(rpc, generation);
      const hello = await rpc.request('host.hello', { ProtocolVersion: PROTOCOL_VERSION, ClientName: CLIENT_NAME });
      if (!hello.Ok)
        throw new Error(`host refused the connection: ${hello.Mismatch ?? 'protocol mismatch'}`);
      this.hello = hello;
      this.#rpc = rpc;
      await this.#reload(rpc);
      this.#failures = 0;
      this.#everConnected = true;
      this.state = 'connected';
      this.error = '';
      this.#resume();
      this.#hub.changed();
    } catch (error) {
      this.#lost(generation, describe(error));
      rpc?.close();
    }
  }

  /** Re-reads everything a disconnect may have missed; pushes that arrive meanwhile are newer and keep applying. */
  async #reload(rpc) {
    const [info, sessions, pending, notifications] = await Promise.all([
      rpc.request('host.info'),
      this.supports(Capability.SessionRegistry)
        ? rpc.request('host.sessions.list')
        : rpc.request('host.session.list').then((list) => list.map(fromHarnessSession)),
      this.supports(Capability.PendingBroadcast) ? rpc.request('host.pending.list') : { Approvals: [], Questions: [] },
      this.supports(Capability.NotifyHistory) ? rpc.request('host.notify.list', { Limit: NOTIFY_PAGE }) : { Items: [], UnreadCount: 0 },
    ]);
    this.info = info;
    this.sessions = new Map(sessions.map((session) => [session.SessionId, session]));
    this.approvals = new Map(pending.Approvals.filter((item) => !this.#resolved.has(item.RequestId)).map((item) => [item.RequestId, item]));
    this.questions = new Map(pending.Questions.filter((item) => !this.#resolved.has(item.RequestId)).map((item) => [item.RequestId, item]));
    this.notifications = notifications.Items;
    this.unread = notifications.UnreadCount;
  }

  #wire(rpc, generation) {
    const live = (handler) => (params) => {
      if (generation === this.#generation)
        handler(params);
    };
    rpc.on('host.sessions.changed', live((session) => this.#changed(() => session.Deleted
      ? this.sessions.delete(session.SessionId)
      : this.sessions.set(session.SessionId, session))));
    rpc.on('host.approval', live((request) => this.#changed(() => this.#addPending(this.approvals, request))));
    rpc.on('host.question', live((request) => this.#changed(() => this.#addPending(this.questions, request))));
    rpc.on('host.pending.resolved', live((resolved) => this.#changed(() => this.#resolve(resolved))));
    rpc.on('host.notify.added', live((record) => this.#changed(() => this.#addNotification(record))));
    rpc.on('host.notify.changed', live((change) => this.#changed(() => this.#readNotifications(change))));
    rpc.on('host.event', live((event) => this.#deliverEvent(event)));
    rpc.on('host.pty.output', live((output) => this.#deliverOutput(output)));
    rpc.onClose((event) => this.#lost(generation, event.reason || `connection closed (${event.code})`));
  }

  #changed(apply) {
    apply();
    this.#hub.changed();
  }

  #addPending(map, request) {
    if (!this.#resolved.has(request.RequestId))
      map.set(request.RequestId, request);
  }

  #resolve(resolved) {
    this.approvals.delete(resolved.RequestId);
    this.questions.delete(resolved.RequestId);
    this.#resolved.add(resolved.RequestId);
    if (this.#resolved.size > RESOLVED_MEMORY)
      this.#resolved.delete(this.#resolved.values().next().value);
    this.#hub.dispatchEvent(new CustomEvent('resolved', { detail: { machine: this.name, resolved } }));
  }

  #addNotification(record) {
    if (this.notifications.some((item) => item.Id === record.Id))
      return;
    this.notifications = [record, ...this.notifications].slice(0, NOTIFY_PAGE);
    if (!record.IsRead)
      this.unread++;
    this.#hub.dispatchEvent(new CustomEvent('notification', { detail: { machine: this.name, record } }));
  }

  #readNotifications(change) {
    const read = new Set(change.ReadIds);
    this.notifications = this.notifications.map((item) => (read.has(item.Id) ? { ...item, IsRead: true } : item));
    this.unread = change.UnreadCount;
  }

  /** Delivers each seq once: a resubscription replays from the next unseen seq, or from 0 when the host rejected that. */
  #deliverEvent({ SessionId, EventJson, Reset }) {
    const subscription = this.#subscriptions.get(SessionId);
    if (subscription === undefined)
      return;
    if (Reset) {
      subscription.pending = [];
      const pending = subscription.pending;
      this.request('host.session.snapshot', { SessionId }).then((snapshot) => {
        if (this.#subscriptions.get(SessionId) !== subscription || subscription.pending !== pending)
          return;
        subscription.pending = null;
        subscription.next = 0;
        subscription.log = [];
        subscription.listeners.forEach((listener) => listener.reset?.());
        [...snapshot.Events, ...pending].forEach((item) => this.#deliverEvent(item));
      }).catch((error) => subscription.listeners.forEach((listener) => listener.error(describe(error))));
      return;
    }
    if (subscription.pending) {
      subscription.pending.push({ SessionId, EventJson });
      return;
    }
    const event = JSON.parse(EventJson);
    if (typeof event.seq !== 'number' || event.seq < subscription.next)
      return;
    subscription.next = event.seq + 1;
    subscription.log.push(event);
    subscription.listeners.forEach((listener) => listener.event(event));
  }

  #deliverOutput({ PtyId, Base64, Eof }) {
    const terminal = this.#terminals.get(PtyId);
    if (terminal === undefined)
      return;
    const snapshot = terminal.snapshotNext;
    terminal.snapshotNext = false;
    const bytes = base64ToBytes(Base64);
    terminal.listeners.forEach((listener) => {
      if (bytes.length > 0 || snapshot)
        listener.data(bytes, snapshot);
      if (Eof)
        listener.eof();
    });
  }

  #subscribeOnHost(sessionId, subscription) {
    if (!this.connected)
      return;
    const from = subscription.next;
    this.request('host.session.subscribe', { SessionId: sessionId, FromSeq: from }).catch((error) => {
      if (from > 0 && this.#subscriptions.get(sessionId) === subscription) {
        this.request('host.session.subscribe', { SessionId: sessionId, FromSeq: 0 })
          .catch((retry) => subscription.listeners.forEach((listener) => listener.error(describe(retry))));
        return;
      }
      subscription.listeners.forEach((listener) => listener.error(describe(error)));
    });
  }

  #attachOnHost(ptyId, terminal) {
    if (!this.connected)
      return;
    terminal.snapshotNext = true;
    this.request('host.pty.attach', { PtyId: ptyId })
      .catch((error) => terminal.listeners.forEach((listener) => listener.error(describe(error))));
  }

  #resume() {
    this.#subscriptions.forEach((subscription, sessionId) => this.#subscribeOnHost(sessionId, subscription));
    this.#terminals.forEach((terminal, ptyId) => this.#attachOnHost(ptyId, terminal));
  }

  /** Schedules the next attempt once per lost connection; a link that never connected keeps its last error visible. */
  #lost(generation, reason) {
    if (generation !== this.#generation || this.state === 'waiting')
      return;
    this.#rpc = null;
    this.state = 'waiting';
    this.error = reason;
    this.#failures++;
    const delay = Math.min(MAX_BACKOFF_MS, INITIAL_BACKOFF_MS * 2 ** (this.#failures - 1));
    this.retryAt = Date.now() + delay;
    this.#retryTimer = setTimeout(() => this.#connect(), delay);
    this.#hub.changed();
  }
}

/** Hosts without the session registry only list harness sessions. */
function fromHarnessSession(session) {
  return {
    SessionId: session.Id, Kind: SessionKind.Harness, AgentKind: 'dsharp', ProjectPath: session.Cwd, Title: session.Title,
    Status: session.Status, CreatedAt: session.StartedAt, LastActivityAt: session.UpdatedAt, PendingCount: 0,
  };
}

export function describe(error) {
  if (error instanceof Event)
    return `bridge closed the WebSocket (${error.code ?? ''}${error.reason ? `: ${error.reason}` : ''})`;
  return error?.message ?? String(error);
}
