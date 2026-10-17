// Light file browsing and git inspection of one machine: list directories, preview text files read-only, download, git status and per-file diffs.
import { h, mount, link, toast, base64ToBytes, bytesToText, baseName, ago, when } from '../dom.js';
import { describe } from '../hub.js';
import { Capability } from '/protocol.js';
import { filesRoute, gitRoute, emptyState } from './common.js';

/** Larger files are offered for download only. */
const PREVIEW_LIMIT_BYTES = 512 * 1024;

/** A NUL byte in the first bytes marks a file as binary. */
const BINARY_SNIFF_BYTES = 8192;

const HOME = '~';

export function filesView(container, hub, route) {
  const machineLink = hub.link(route.machine);
  if (machineLink === undefined) {
    mount(container, emptyState(`No machine named ${route.machine}.`));
    return {};
  }
  const tabs = h('nav', { className: 'tabs', 'aria-label': 'Files or git' },
    link(filesRoute(route.machine, route.path ?? route.project), { 'aria-current': route.tab === 'files' ? 'page' : 'false' }, 'Files'),
    link(gitRoute(route.machine, route.project ?? route.path ?? HOME), { 'aria-current': route.tab === 'git' ? 'page' : 'false' }, 'Git'));
  const body = h('div', { className: 'files-body' });
  mount(container, h('div', { className: 'view-head' }, h('h1', {}, route.machine), tabs), body);
  let loaded = false;

  function update() {
    if (loaded || !machineLink.connected)
      return;
    loaded = true;
    if (route.tab === 'git')
      gitPanel(body, machineLink, route.project ?? HOME);
    else
      filePanel(body, machineLink, route.path ?? HOME);
  }

  mount(body, emptyState(`Waiting for ${route.machine} to connect…`));
  update();
  return { update };
}

function filePanel(body, machineLink, path) {
  const location = h('input', { type: 'text', id: 'file-path', value: path, 'aria-label': 'Path', autocapitalize: 'off', spellcheck: false });
  const listing = h('ul', { className: 'file-list', id: 'file-list' });
  const preview = h('section', { className: 'file-preview', id: 'file-preview', hidden: true });
  const go = h('form', { className: 'path-bar', onSubmit: (event) => {
    event.preventDefault();
    window.location.hash = `#${filesRoute(machineLink.name, location.value.trim() || HOME)}`;
  } }, location, h('button', { type: 'submit' }, 'Go'));
  mount(body, go, h('div', { className: 'files-split' }, listing, preview));
  listDirectory(machineLink, path, listing, preview, location);
}

async function listDirectory(machineLink, path, listing, preview, location) {
  try {
    const directory = await machineLink.request('host.file.list', { Path: path });
    location.value = directory.Path;
    const entries = [...directory.Entries].sort((left, right) => Number(right.IsDirectory) - Number(left.IsDirectory) || left.Name.localeCompare(right.Name));
    mount(listing,
      directory.Parent ? h('li', {}, link(filesRoute(machineLink.name, directory.Parent), { className: 'file-row directory' }, '..')) : null,
      entries.length === 0 ? h('li', {}, emptyState('This directory is empty.')) : null,
      entries.map((entry) => h('li', {}, entry.IsDirectory
        ? link(filesRoute(machineLink.name, entry.Path), { className: 'file-row directory' }, `${entry.Name}/`)
        : h('button', { type: 'button', className: 'file-row', onClick: () => showFile(machineLink, entry.Path, preview) }, entry.Name))));
  } catch (error) {
    mount(listing, h('li', {}, h('p', { className: 'error' }, describe(error))));
  }
}

async function showFile(machineLink, path, preview) {
  preview.hidden = false;
  const download = h('button', { type: 'button', onClick: () => saveFile(machineLink, path) }, 'Download');
  const content = h('div', {}, emptyState('Loading…'));
  mount(preview, h('header', { className: 'preview-head' }, h('h2', { title: path }, baseName(path)), download), content);
  preview.scrollIntoView({ block: 'nearest' });
  try {
    const stat = machineLink.supports(Capability.FsTransfer) ? await machineLink.request('host.fs.stat', { Path: path }) : null;
    if (stat !== null && stat.Size > PREVIEW_LIMIT_BYTES) {
      mount(content, emptyState(`${formatSize(stat.Size)}: too large to preview; download it instead.`));
      return;
    }
    const bytes = await readFile(machineLink, path);
    const binary = bytes.subarray(0, BINARY_SNIFF_BYTES).includes(0);
    mount(content,
      stat ? h('p', { className: 'muted small' }, `${formatSize(stat.Size)} · modified `, h('time', { title: when(stat.ModifiedAt) }, ago(stat.ModifiedAt))) : null,
      binary ? emptyState('Binary file; download it to open.') : h('pre', { className: 'file-text', id: 'file-text' }, bytesToText(bytes)));
  } catch (error) {
    mount(content, h('p', { className: 'error' }, describe(error)));
  }
}

/** Whole file bytes: chunked host.fs.download where available, host.file.read otherwise. */
async function readFile(machineLink, path) {
  if (!machineLink.supports(Capability.FsTransfer))
    return base64ToBytes((await machineLink.request('host.file.read', { Path: path })).Base64);
  const chunks = [];
  let offset = 0;
  while (true) {
    const chunk = await machineLink.request('host.fs.download', { Path: path, Offset: offset });
    const bytes = base64ToBytes(chunk.Base64);
    chunks.push(bytes);
    offset += bytes.length;
    if (chunk.Eof || bytes.length === 0)
      break;
  }
  const whole = new Uint8Array(offset);
  let position = 0;
  for (const chunk of chunks) {
    whole.set(chunk, position);
    position += chunk.length;
  }
  return whole;
}

async function saveFile(machineLink, path) {
  try {
    const bytes = await readFile(machineLink, path);
    const url = URL.createObjectURL(new Blob([bytes], { type: 'application/octet-stream' }));
    const anchor = h('a', { href: url, download: baseName(path) });
    document.body.append(anchor);
    anchor.click();
    anchor.remove();
    setTimeout(() => URL.revokeObjectURL(url), 0);
  } catch (error) {
    toast(`Download failed: ${describe(error)}`, 'error');
  }
}

function formatSize(bytes) {
  const units = ['B', 'KiB', 'MiB', 'GiB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${unit === 0 ? value : value.toFixed(1)} ${units[unit]}`;
}

function gitPanel(body, machineLink, project) {
  const location = h('input', { type: 'text', id: 'git-project', value: project, 'aria-label': 'Project path', autocapitalize: 'off', spellcheck: false });
  const status = h('div', { className: 'git-status', id: 'git-status' }, emptyState('Loading…'));
  const diff = h('section', { className: 'git-diff', id: 'git-diff', hidden: true });
  const go = h('form', { className: 'path-bar', onSubmit: (event) => {
    event.preventDefault();
    window.location.hash = `#${gitRoute(machineLink.name, location.value.trim() || HOME)}`;
  } }, location, h('button', { type: 'submit' }, 'Open'));
  mount(body, go, h('div', { className: 'files-split' }, status, diff));
  if (!machineLink.supports(Capability.GitInspect)) {
    mount(status, emptyState('This host does not offer git inspection.'));
    return;
  }
  loadStatus(machineLink, project, status, diff);
}

async function loadStatus(machineLink, project, status, diff) {
  try {
    const result = await machineLink.request('host.git.status', { ProjectPath: project });
    const branch = result.Branch;
    const tracking = branch.Upstream ? ` → ${branch.Upstream}${branch.Ahead || branch.Behind ? ` (+${branch.Ahead ?? 0} −${branch.Behind ?? 0})` : ''}` : '';
    const section = (title, changes, source) => (changes.length === 0 ? null : h('section', {},
      h('h3', {}, `${title} (${changes.length})`),
      h('ul', { className: 'file-list' }, changes.map((change) => h('li', {},
        h('button', { type: 'button', className: 'file-row', onClick: () => showDiff(machineLink, project, change, source, diff) },
          h('span', { className: `chip change-${(change.Kind ?? 'untracked').toLowerCase()}` }, change.Kind ?? 'New'), change.Path))))));
    const untracked = result.Untracked.map((path) => ({ Path: path }));
    mount(status,
      h('p', {}, h('strong', {}, branch.Head ?? `detached ${branch.Oid?.slice(0, 8) ?? ''}`), tracking),
      section('Conflicted', result.Conflicted, 'WorkingTree'),
      section('Staged', result.Staged, 'Staged'),
      section('Changed', result.Unstaged, 'WorkingTree'),
      section('Untracked', untracked, null),
      result.IsClean ? emptyState('Working tree clean.') : null);
  } catch (error) {
    mount(status, h('p', { className: 'error' }, describe(error)));
  }
}

/** Untracked files have no diff; their content is shown instead. */
async function showDiff(machineLink, project, change, source, diff) {
  diff.hidden = false;
  mount(diff, h('h2', {}, change.Path), emptyState('Loading…'));
  diff.scrollIntoView({ block: 'nearest' });
  if (source === null) {
    await showFile(machineLink, joinPath(project, change.Path), diff);
    return;
  }
  try {
    const result = await machineLink.request('host.git.diff', { ProjectPath: project, Target: { Source: source }, Paths: [change.Path] });
    const file = result.Files[0];
    mount(diff, h('h2', {}, change.Path),
      file === undefined ? emptyState('No differences.') : file.Added === null || file.Added === undefined ? emptyState('Binary file changed.') : patchView(file));
  } catch (error) {
    mount(diff, h('h2', {}, change.Path), h('p', { className: 'error' }, describe(error)));
  }
}

function patchView(file) {
  return h('pre', { className: 'diff', id: 'diff-text' }, file.Patch.split('\n').map((line) => h('span', { className: `diff-line ${lineKind(line)}` }, `${line}\n`)),
    file.Truncated ? h('span', { className: 'muted' }, '… (truncated)') : null);
}

function lineKind(line) {
  if (line.startsWith('@@'))
    return 'hunk';
  if (line.startsWith('+') && !line.startsWith('+++'))
    return 'added';
  if (line.startsWith('-') && !line.startsWith('---'))
    return 'removed';
  return '';
}

function joinPath(directory, relative) {
  return `${directory.replace(/[\\/]+$/, '')}/${relative}`;
}
