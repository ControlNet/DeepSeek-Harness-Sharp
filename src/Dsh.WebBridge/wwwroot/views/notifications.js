// Notification center: history of every machine (newest first), unread state shared with every other client of the host.
import { h, mount, ago, when, toast } from '../dom.js';
import { describe } from '../hub.js';
import { sessionRoute, emptyState } from './common.js';
import { NotificationKind, NotificationView } from '/protocol.js';

const KIND_LABELS = {
  [NotificationKind.WaitingForInput]: 'Waiting for input',
  [NotificationKind.ApprovalRequest]: 'Approval',
  [NotificationKind.Question]: 'Question',
  [NotificationKind.SessionExited]: 'Exited',
  [NotificationKind.Error]: 'Error',
  [NotificationKind.Info]: 'Info',
};

let unreadOnly = false;

export function notificationsView(container, hub) {
  const list = h('ul', { className: 'notification-list', id: 'notification-list' });
  const filter = h('button', {
    type: 'button', className: 'toggle', 'aria-pressed': String(unreadOnly),
    onClick: () => {
      unreadOnly = !unreadOnly;
      filter.setAttribute('aria-pressed', String(unreadOnly));
      update();
    },
  }, 'Unread only');
  const markAll = h('button', { type: 'button', id: 'mark-all-read', onClick: () => markAllRead(hub) }, 'Mark all read');
  mount(container, h('div', { className: 'view-head' }, h('h1', {}, 'Notifications'), h('div', { className: 'actions' }, filter, markAll)), list);

  function update() {
    const items = hub.notifications().filter((item) => !unreadOnly || !item.record.IsRead);
    markAll.disabled = hub.unreadCount() === 0;
    mount(list, items.length === 0 ? h('li', {}, emptyState('No notifications.')) : items.map((item) => notificationRow(hub, item)));
  }

  update();
  return { update };
}

function notificationRow(hub, { machine, record }) {
  const open = async () => {
    if (!record.IsRead)
      await hub.link(machine)?.markRead([record.Id]).catch((error) => toast(`Could not mark as read: ${describe(error)}`, 'error'));
    location.hash = target(machine, record);
  };
  return h('li', {},
    h('button', { type: 'button', className: `notification${record.IsRead ? '' : ' unread'}`, dataset: { id: record.Id, read: String(record.IsRead) }, onClick: open },
      h('span', { className: `chip notify-${record.Kind.toLowerCase()}` }, KIND_LABELS[record.Kind] ?? record.Kind),
      h('span', { className: 'notification-main' },
        h('span', { className: 'notification-title' }, record.Title),
        record.Body ? h('span', { className: 'muted' }, record.Body) : null,
        h('span', { className: 'muted small' }, machine, ' · ', h('time', { title: when(record.CreatedAt) }, ago(record.CreatedAt)))),
      record.IsRead ? null : h('span', { className: 'dot', 'aria-label': 'unread' })));
}

/** Where clicking leads: the session, the pending decisions, or the dashboard. */
function target(machine, record) {
  if (record.Target?.View === NotificationView.PendingRequest)
    return '#/pending';
  if (record.SessionId)
    return `#${sessionRoute(machine, record.SessionId)}`;
  return '#/';
}

async function markAllRead(hub) {
  const results = await Promise.allSettled([...hub.links.values()].filter((machineLink) => machineLink.connected && machineLink.unread > 0).map((machineLink) => machineLink.markRead()));
  const failed = results.find((result) => result.status === 'rejected');
  if (failed)
    toast(`Could not mark everything as read: ${describe(failed.reason)}`, 'error');
}
