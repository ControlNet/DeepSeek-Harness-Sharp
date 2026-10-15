// New session: machine, agent (host.agent.catalog), mode, project path, optional worktree and initial prompt; started with host.agent.start.
import { h, mount, toast } from '../dom.js';
import { Capability, describe } from '../hub.js';
import { sessionRoute, emptyState } from './common.js';

const MODE_TERMINAL = 'terminal';
const MODE_ACP = 'acp';

/** Catalogs are read once per machine and page. */
const catalogs = new Map();

export function newSessionView(container, hub, route) {
  const connected = [...hub.links.values()].filter((machineLink) => machineLink.connected && machineLink.supports(Capability.AgentSessions));
  if (connected.length === 0) {
    mount(container, h('h1', {}, 'New session'), emptyState('No connected machine can start sessions right now.'));
    // The form appears as soon as a machine connects; from then on this controller is the form's.
    const waiting = {
      update() {
        if ([...hub.links.values()].some((machineLink) => machineLink.connected))
          Object.assign(waiting, { update: undefined }, newSessionView(container, hub, route));
      },
    };
    return waiting;
  }

  const machine = h('select', { id: 'new-machine', name: 'machine' }, connected.map((machineLink) => h('option', { value: machineLink.name, selected: machineLink.name === route.machine }, machineLink.name)));
  const agent = h('select', { id: 'new-agent', name: 'agent' });
  const mode = h('select', { id: 'new-mode', name: 'mode' });
  const command = h('input', { id: 'new-command', name: 'command', type: 'text', placeholder: 'e.g. bash -l', autocapitalize: 'off', spellcheck: false });
  const projects = h('datalist', { id: 'new-projects' });
  const project = h('input', { id: 'new-project', name: 'project', type: 'text', required: true, value: route.project ?? '~', list: 'new-projects', autocapitalize: 'off', spellcheck: false });
  const title = h('input', { id: 'new-title', name: 'title', type: 'text', placeholder: 'Optional' });
  const prompt = h('textarea', { id: 'new-prompt', name: 'prompt', rows: 3, placeholder: 'Optional first message' });
  const worktreePath = h('input', { id: 'new-worktree', type: 'text', placeholder: 'e.g. ../feature-x', autocapitalize: 'off', spellcheck: false });
  const worktreeBranch = h('input', { id: 'new-branch', type: 'text', placeholder: 'Optional new branch', autocapitalize: 'off', spellcheck: false });
  const submit = h('button', { type: 'submit', className: 'primary', id: 'new-start' }, 'Start');
  const commandRow = field('Command', command, 'Executable and arguments; "{prompt}" is replaced by the first message.');
  const modeRow = field('Mode', mode);
  let entries = [];

  const form = h('form', { className: 'new-session card', onSubmit: (event) => start(event) },
    field('Machine', machine),
    field('Agent', agent),
    modeRow,
    commandRow,
    field('Project path', project, 'Directory on that machine; ~ is its home.'),
    projects,
    field('Title', title),
    field('First message', prompt),
    h('details', { className: 'worktree' }, h('summary', {}, 'Git worktree'), field('Worktree path', worktreePath, 'Relative paths are inside the project.'), field('New branch', worktreeBranch)),
    h('div', { className: 'actions' }, submit));
  mount(container, h('h1', {}, 'New session'), form);

  machine.addEventListener('change', () => loadCatalog());
  agent.addEventListener('change', () => showAgentFields());

  async function loadCatalog() {
    const machineLink = hub.link(machine.value);
    mount(projects, ...[...new Set([...machineLink.sessions.values()].map((session) => session.ProjectPath).filter(Boolean))].map((path) => h('option', { value: path })));
    try {
      if (!catalogs.has(machineLink.name))
        catalogs.set(machineLink.name, await machineLink.request('host.agent.catalog'));
      entries = catalogs.get(machineLink.name);
    } catch (error) {
      entries = [];
      toast(`Cannot read the agent list of ${machineLink.name}: ${describe(error)}`, 'error');
    }
    mount(agent, entries.map((entry) => h('option', { value: entry.AgentKind, disabled: !entry.Available && !entry.RequiresCommand },
      `${entry.DisplayName}${entry.Available || entry.RequiresCommand ? '' : ' (not installed)'}`)));
    showAgentFields();
  }

  function showAgentFields() {
    const entry = entries.find((item) => item.AgentKind === agent.value);
    const modes = entry === undefined ? [] : [entry.SupportsTerminal ? MODE_TERMINAL : null, entry.SupportsAcp ? MODE_ACP : null].filter(Boolean);
    mount(mode, modes.map((value) => h('option', { value }, value === MODE_ACP ? 'ACP (structured)' : 'Terminal')));
    modeRow.hidden = modes.length < 2;
    commandRow.hidden = !entry?.RequiresCommand;
    command.required = Boolean(entry?.RequiresCommand);
  }

  async function start(event) {
    event.preventDefault();
    const machineLink = hub.link(machine.value);
    const entry = entries.find((item) => item.AgentKind === agent.value);
    const request = {
      AgentKind: agent.value,
      ProjectPath: project.value.trim(),
      InitialPrompt: prompt.value.trim() || undefined,
      Mode: modeRow.hidden ? undefined : mode.value,
      Command: entry?.RequiresCommand ? parseCommand(command.value) : undefined,
      Worktree: worktreePath.value.trim() ? { Path: worktreePath.value.trim(), NewBranch: worktreeBranch.value.trim() || undefined } : undefined,
      Title: title.value.trim() || undefined,
    };
    submit.disabled = true;
    try {
      const session = await machineLink.request('host.agent.start', request);
      location.hash = `#${sessionRoute(machineLink.name, session.SessionId)}`;
    } catch (error) {
      toast(`Could not start: ${describe(error)}`, 'error');
      submit.disabled = false;
    }
  }

  loadCatalog();
  return {};
}

function field(label, control, hint) {
  return h('div', { className: 'field' },
    h('label', { for: control.id }, label),
    control,
    hint ? h('p', { className: 'hint' }, hint) : null);
}

/** Splits a command line on whitespace, keeping single- or double-quoted parts together. */
export function parseCommand(line) {
  const parts = [...line.matchAll(/"([^"]*)"|'([^']*)'|(\S+)/g)].map((match) => match[1] ?? match[2] ?? match[3]);
  return { Executable: parts[0] ?? '', Arguments: parts.slice(1) };
}
