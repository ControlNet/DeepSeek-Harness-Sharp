// Dashboard: every machine, its projects and their sessions, with totals, an attention filter and search.
import { h, mount, link, ago, when } from '../dom.js';
import { SessionStatus, needsAttention } from '../hub.js';
import { sessionRoute, sessionTitle, statusChip, machineChip, emptyState, filesRoute, gitRoute } from './common.js';

/** Filters survive navigating away and back within the page. */
const filters = { query: '', attentionOnly: false };

export function dashboardView(container, hub) {
  const summary = h('section', { className: 'summary', 'aria-label': 'Summary' });
  const machines = h('div', { className: 'machines' });
  const search = h('input', {
    type: 'search', id: 'session-search', placeholder: 'Search sessions, projects, agents', value: filters.query, 'aria-label': 'Search sessions',
    onInput: () => {
      filters.query = search.value;
      update();
    },
  });
  const attention = h('button', {
    type: 'button', id: 'attention-filter', className: 'toggle', 'aria-pressed': String(filters.attentionOnly),
    onClick: () => {
      filters.attentionOnly = !filters.attentionOnly;
      attention.setAttribute('aria-pressed', String(filters.attentionOnly));
      update();
    },
  }, 'Needs attention');
  mount(container, h('h1', { className: 'visually-hidden' }, 'Dashboard'), summary, h('div', { className: 'toolbar' }, search, attention), machines);

  function update() {
    renderSummary(summary, hub);
    mount(machines, ...[...hub.links.values()].map((machineLink) => machineSection(machineLink)));
  }

  update();
  return { update };
}

function renderSummary(summary, hub) {
  const sessions = hub.sessions().map((item) => item.session);
  const running = sessions.filter((session) => session.Status === SessionStatus.Running).length;
  const attention = sessions.filter(needsAttention).length;
  const offline = hub.offlineCount();
  mount(summary,
    tile('Running', running, 'running', 'tile-running'),
    tile('Needs attention', attention, attention > 0 ? 'attention' : '', 'tile-attention'),
    tile('Offline machines', `${offline}/${hub.links.size}`, offline > 0 ? 'offline' : '', 'tile-offline'));
}

function tile(label, value, tone, id) {
  return h('div', { className: `tile ${tone}`, id }, h('span', { className: 'tile-value' }, String(value)), h('span', { className: 'tile-label' }, label));
}

function machineSection(machineLink) {
  const sessions = [...machineLink.sessions.values()].filter(matchesFilters);
  const projects = groupByProject(sessions);
  const version = machineLink.hello?.HostVersion ?? machineLink.bridgeStatus?.HostVersion;
  const problem = machineLink.connected ? null : machineLink.error || machineLink.bridgeStatus?.Detail;
  return h('section', { className: 'machine card', dataset: { machine: machineLink.name } },
    h('header', { className: 'machine-head' },
      h('div', { className: 'machine-title' },
        h('h2', {}, machineLink.name),
        machineChip(machineLink),
        version ? h('span', { className: 'muted host-version' }, `dsharp ${version}`) : null),
      h('div', { className: 'machine-actions' },
        link(`/new?machine=${encodeURIComponent(machineLink.name)}`, { className: 'button small' }, 'New'),
        link(filesRoute(machineLink.name), { className: 'button small' }, 'Files'))),
    machineLink.bridgeStatus?.Target ? h('p', { className: 'muted target' }, machineLink.bridgeStatus.Target) : null,
    problem ? h('p', { className: 'error small' }, problem) : null,
    projects.length === 0
      ? emptyState(machineLink.sessions.size === 0 ? 'No sessions on this machine.' : 'No session matches the filters.')
      : projects.map(([project, items]) => projectGroup(machineLink.name, project, items)));
}

function projectGroup(machine, project, sessions) {
  return h('div', { className: 'project' },
    h('div', { className: 'project-head' },
      h('h3', { title: project ?? '' }, project ?? 'No project'),
      project ? link(gitRoute(machine, project), { className: 'muted small' }, 'Git') : null),
    h('ul', { className: 'session-list' }, sessions.map((session) => h('li', {}, sessionRow(machine, session)))));
}

function sessionRow(machine, session) {
  const attention = needsAttention(session);
  return link(sessionRoute(machine, session.SessionId), { className: `session-row${attention ? ' attention' : ''}`, dataset: { session: session.SessionId, status: session.Status } },
    h('span', { className: 'session-main' },
      h('span', { className: 'session-title' }, sessionTitle(session)),
      h('span', { className: 'session-meta muted' },
        [session.AgentKind ?? session.Kind, session.Kind, session.ExitCode !== undefined && session.ExitCode !== null ? `exit ${session.ExitCode}` : null]
          .filter(Boolean).join(' · '),
        ' · ',
        h('time', { title: when(session.LastActivityAt) }, ago(session.LastActivityAt)))),
    h('span', { className: 'session-side' },
      session.PendingCount > 0 ? h('span', { className: 'badge attention', title: 'Unanswered approvals and questions' }, String(session.PendingCount)) : null,
      statusChip(session.Status)));
}

function matchesFilters(session) {
  if (filters.attentionOnly && !needsAttention(session))
    return false;
  const query = filters.query.trim().toLowerCase();
  if (query.length === 0)
    return true;
  return [session.Title, session.ProjectPath, session.AgentKind, session.Kind, session.SessionId, session.Status]
    .some((field) => (field ?? '').toLowerCase().includes(query));
}

/** Project → sessions, projects by path and sessions newest activity first. */
function groupByProject(sessions) {
  const groups = new Map();
  for (const session of sessions) {
    const key = session.ProjectPath ?? null;
    groups.set(key, [...(groups.get(key) ?? []), session]);
  }
  return [...groups.entries()]
    .sort(([left], [right]) => (left ?? '').localeCompare(right ?? ''))
    .map(([project, items]) => [project, items.sort((left, right) => right.LastActivityAt - left.LastActivityAt)]);
}
