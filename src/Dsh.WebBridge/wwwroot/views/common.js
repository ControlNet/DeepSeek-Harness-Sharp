// Pieces shared by several views: routes, session labels, status chips and keyed list updates.
import { h, baseName } from '../dom.js';
import { SessionStatus } from '/protocol.js';

export function sessionRoute(machine, sessionId) {
  return `/m/${encodeURIComponent(machine)}/s/${encodeURIComponent(sessionId)}`;
}

export function filesRoute(machine, path) {
  return `/m/${encodeURIComponent(machine)}/files${path ? `?path=${encodeURIComponent(path)}` : ''}`;
}

export function gitRoute(machine, project) {
  return `/m/${encodeURIComponent(machine)}/git?project=${encodeURIComponent(project)}`;
}

export function sessionTitle(session) {
  return session.Title || baseName(session.ProjectPath) || session.SessionId;
}

const STATUS_LABELS = {
  [SessionStatus.Running]: 'Running',
  [SessionStatus.WaitingForUser]: 'Waiting for you',
  [SessionStatus.Idle]: 'Idle',
  [SessionStatus.Exited]: 'Exited',
};

export function statusChip(status) {
  return h('span', { className: `chip status-${(status ?? 'unknown').toLowerCase()}` }, STATUS_LABELS[status] ?? status ?? 'Unknown');
}

const LINK_LABELS = { connected: 'Connected', connecting: 'Connecting', reconnecting: 'Reconnecting', waiting: 'Offline' };

/** The page's own connection to a machine, with the bridge's view (deploying, offline reason) when the page cannot reach it. */
export function machineChip(link) {
  const bridgeState = link.bridgeStatus?.State;
  const label = !link.connected && bridgeState === 'Deploying' ? 'Deploying' : LINK_LABELS[link.state] ?? link.state;
  return h('span', { className: `chip machine-${link.connected ? 'online' : 'offline'}`, dataset: { state: link.state } }, label);
}

export function emptyState(text) {
  return h('p', { className: 'empty' }, text);
}

/**
 * Updates a list in place: elements are keyed by data-key, existing ones are kept (so their inputs and focus survive), missing ones are created and stale ones removed.
 * render(item) builds a new element; refresh(element, item), when given, updates a kept one.
 */
export function syncList(parent, items, key, render, refresh) {
  const existing = new Map([...parent.children].map((child) => [child.dataset.key, child]));
  const ordered = items.map((item) => {
    const id = key(item);
    let element = existing.get(id);
    if (element === undefined) {
      element = render(item);
      element.dataset.key = id;
    } else
      refresh?.(element, item);
    existing.delete(id);
    return element;
  });
  existing.forEach((stale) => stale.remove());
  ordered.forEach((element, index) => {
    if (parent.children[index] !== element)
      parent.insertBefore(element, parent.children[index] ?? null);
  });
}

/** Pretty-prints JSON text, or returns it unchanged when it is not JSON. */
export function prettyJson(text) {
  if (typeof text !== 'string' || text.length === 0)
    return '';
  try {
    return JSON.stringify(JSON.parse(text), null, 2);
  } catch {
    return text;
  }
}
