// Presentation only. The server's shared Fleet connection owns machine reconnect, capability fallback,
// pending decisions, resumable events and PTY reattachment. Browser transport loss rebuilds views from
// authoritative server streams; no event log, sequence filter or resolved-request memory lives here.
import { HostRpc, bridgeUrl } from './rpc.js';
import { base64ToBytes, textToBase64 } from './dom.js';
import { SessionStatus, SessionKind } from '/protocol.js';

const MACHINE_POLL_MS = 5000;

export function isTerminalSession(session) { return session.Kind === SessionKind.Terminal || session.Kind === SessionKind.External; }
export function needsAttention(session) { return session.Status === SessionStatus.WaitingForUser || session.PendingCount > 0; }

export class Hub extends EventTarget {
  links = new Map();
  machines = [];
  #changeQueued = false;

  async start() {
    await this.refreshMachines();
    setInterval(() => this.refreshMachines().catch(() => {}), MACHINE_POLL_MS);
    window.addEventListener('online', () => this.refreshMachines().catch(() => {}));
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'visible')
        this.refreshMachines().catch(() => {});
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
      }
      link.bridgeStatus = status;
      // This reconnects only the browser-to-bridge transport. It does not connect or retry a host.
      link.open();
    }
    this.changed();
  }

  changed() {
    if (this.#changeQueued)
      return;
    this.#changeQueued = true;
    queueMicrotask(() => { this.#changeQueued = false; this.dispatchEvent(new Event('change')); });
  }
  link(machine) { return this.links.get(machine); }
  sessions() { return [...this.links.values()].flatMap((link) => [...link.sessions.values()].map((session) => ({ machine: link.name, session }))); }
  pending() {
    return [...this.links.values()].flatMap((link) => [
      ...[...link.approvals.values()].map((request) => ({ machine: link.name, kind: 'approval', request })),
      ...[...link.questions.values()].map((request) => ({ machine: link.name, kind: 'question', request })),
    ]);
  }
  notifications() {
    return [...this.links.values()].flatMap((link) => link.notifications.map((record) => ({ machine: link.name, record })))
      .sort((left, right) => Date.parse(right.record.CreatedAt) - Date.parse(left.record.CreatedAt));
  }
  unreadCount() { return [...this.links.values()].reduce((sum, link) => sum + link.unread, 0); }
  offlineCount() { return [...this.links.values()].filter((link) => !link.connected).length; }
}

/** One authoritative render view and the UI's active stream listeners, not a machine connection. */
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
  #opening = false;
  #subscriptions = new Map();
  #terminals = new Map();

  constructor(name, hub) { this.name = name; this.#hub = hub; }
  get connected() { return this.#rpc?.isOpen === true && this.state === 'connected'; }
  supports(capability) { return (this.hello?.Capabilities ?? []).includes(capability); }

  async open() {
    if (this.#rpc !== null || this.#opening)
      return;
    this.#opening = true;
    try {
      const rpc = await HostRpc.connect(bridgeUrl(this.name));
      this.#rpc = rpc;
      rpc.on('bridge.view', (view) => {
        this.state = view.State === 'Connected' ? 'connected' : view.State === 'Offline' ? 'waiting' : 'connecting';
        this.error = view.Detail ?? '';
        this.info = view.Info;
        this.hello = view.Hello ?? this.hello;
        this.sessions = new Map(view.Sessions.map((session) => [session.SessionId, session]));
        this.approvals = new Map(view.Pending.Approvals.map((request) => [request.RequestId, request]));
        this.questions = new Map(view.Pending.Questions.map((request) => [request.RequestId, request]));
        if (view.Notifications !== null) {
          this.notifications = view.Notifications.Items;
          this.unread = view.Notifications.UnreadCount;
        }
        this.#hub.changed();
      });
      rpc.on('host.event', ({ ViewId, EventJson, Reset }) => {
        const listener = this.#subscriptions.get(ViewId)?.listener;
        if (Reset)
          listener?.reset?.();
        else
          listener?.event(JSON.parse(EventJson));
      });
      rpc.on('host.pty.output', ({ ViewId, Base64, Eof, Snapshot }) => {
        const listener = this.#terminals.get(ViewId)?.listener;
        if (listener === undefined)
          return;
        const bytes = base64ToBytes(Base64);
        if (bytes.length > 0 || Snapshot)
          listener.data(bytes, Snapshot);
        if (Eof)
          listener.eof();
      });
      rpc.on('bridge.stream.error', ({ Kind, ViewId, Message }) => {
        (Kind === 'session' ? this.#subscriptions : this.#terminals).get(ViewId)?.listener.error(Message);
      });
      rpc.onClose((event) => {
        if (this.#rpc !== rpc)
          return;
        this.#rpc = null;
        this.state = 'waiting';
        this.error = event.reason || 'Connection to the web bridge lost.';
        this.#hub.changed();
      });
      // A new browser transport has no previous stream state. Rebuild the currently rendered views;
      // host reconnect while this socket remains open is handled entirely by Fleet on the server.
      this.#subscriptions.forEach((view, viewId) => {
        view.listener.reset?.();
        this.#followSession(viewId, view);
      });
      this.#terminals.forEach((view, viewId) => this.#showTerminal(viewId, view));
    } catch (error) {
      this.state = 'waiting';
      this.error = describe(error);
      this.#hub.changed();
    } finally {
      this.#opening = false;
    }
  }

  request(method, params) {
    return this.#rpc?.request(method, params) ?? Promise.reject(new Error(`${this.name}: web bridge is not connected`));
  }

  subscribe(sessionId, listener) {
    const viewId = crypto.randomUUID();
    const view = { sessionId, listener };
    this.#subscriptions.set(viewId, view);
    this.#followSession(viewId, view);
    return () => {
      this.#subscriptions.delete(viewId);
      this.request('host.session.unsubscribe', { SessionId: sessionId, ViewId: viewId }).catch(() => {});
    };
  }

  #followSession(viewId, view) {
    if (this.#rpc === null)
      return;
    this.request('host.session.subscribe', { SessionId: view.sessionId, ViewId: viewId, FromSeq: 0 })
      .catch((error) => this.#subscriptions.get(viewId)?.listener.error(describe(error)));
  }

  attachTerminal(ptyId, listener) {
    const viewId = crypto.randomUUID();
    const view = { ptyId, listener };
    this.#terminals.set(viewId, view);
    this.#showTerminal(viewId, view);
    return () => {
      this.#terminals.delete(viewId);
      this.request('host.pty.detach', { PtyId: ptyId, ViewId: viewId }).catch(() => {});
    };
  }

  #showTerminal(viewId, view) {
    if (this.#rpc === null)
      return;
    const size = view.listener.size?.();
    this.request('host.pty.attach', { PtyId: view.ptyId, ViewId: viewId, Columns: size?.columns, Rows: size?.rows })
      .catch((error) => this.#terminals.get(viewId)?.listener.error(describe(error)));
  }

  writeTerminal(ptyId, text) { return this.request('host.pty.write', { PtyId: ptyId, Base64: textToBase64(text) }); }
  resizeTerminal(ptyId, columns, rows) {
    const viewId = [...this.#terminals].find(([, view]) => view.ptyId === ptyId)?.[0];
    return this.request('host.pty.resize', { PtyId: ptyId, ViewId: viewId, Columns: columns, Rows: rows });
  }
  markRead(ids) { return this.request('host.notify.markRead', ids === undefined ? { All: true } : { Ids: ids }); }
}

export function describe(error) {
  if (error instanceof Event)
    return `bridge closed the WebSocket (${error.code ?? ''}${error.reason ? `: ${error.reason}` : ''})`;
  return error?.message ?? String(error);
}
