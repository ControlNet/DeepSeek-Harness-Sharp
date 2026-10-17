// Entry point of the web UI: login, the shell (navigation, status, theme), and the hash router that mounts one view at a time.
import { Hub } from './hub.js';
import { h, mount } from './dom.js';
import { dashboardView } from './views/dashboard.js';
import { sessionView } from './views/session.js';
import { pendingView } from './views/pending.js';
import { notificationsView } from './views/notifications.js';
import { newSessionView } from './views/new-session.js';
import { filesView } from './views/files.js';

const status = document.getElementById('status');
const loginForm = document.getElementById('login');
const loginError = document.getElementById('login-error');
const shell = document.getElementById('shell');
const view = document.getElementById('view');
const nav = document.getElementById('nav');
const navToggle = document.getElementById('nav-toggle');

const THEME_KEY = 'dsh-theme';
const THEMES = ['auto', 'light', 'dark'];

let hub = null;
let current = null;

function setStatus(state, text) {
  status.dataset.state = state;
  status.textContent = text;
}

/** Exchanges the token for the HttpOnly session cookie; the bridge answers 204 on success. */
async function login(token) {
  const response = await fetch('/api/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ token }),
    credentials: 'same-origin',
  });
  return response.status === 204;
}

async function hasSession() {
  const response = await fetch('/api/session', { credentials: 'same-origin', cache: 'no-store' });
  return response.status === 204;
}

/** Reads a one-time #token=... and removes it from the address bar and history right away. */
function takeFragmentToken() {
  const token = new URLSearchParams(location.hash.slice(1)).get('token');
  if (token !== null)
    history.replaceState(null, '', `${location.pathname}${location.search}#/`);
  return token;
}

/** `#/m/<machine>/s/<id>`, `#/m/<machine>/files?path=`, `#/m/<machine>/git?project=`, `#/pending`, `#/notifications`, `#/new?machine=`, otherwise the dashboard. */
function parseRoute() {
  const [path, query = ''] = location.hash.replace(/^#/, '').split('?');
  const parts = path.split('/').filter((part) => part.length > 0).map(decodeURIComponent);
  const params = new URLSearchParams(query);
  if (parts[0] === 'm' && parts.length >= 3) {
    const machine = parts[1];
    if (parts[2] === 's' && parts[3] !== undefined)
      return { name: 'session', machine, sessionId: parts[3] };
    if (parts[2] === 'files' || parts[2] === 'git')
      return { name: 'files', machine, tab: parts[2], path: params.get('path'), project: params.get('project') };
  }
  if (parts[0] === 'pending' || parts[0] === 'notifications')
    return { name: parts[0] };
  if (parts[0] === 'new')
    return { name: 'new', machine: params.get('machine'), project: params.get('project') };
  return { name: 'dashboard' };
}

const VIEWS = {
  dashboard: dashboardView,
  session: sessionView,
  pending: pendingView,
  notifications: notificationsView,
  new: newSessionView,
  files: filesView,
};

function render() {
  current?.dispose?.();
  const route = parseRoute();
  mount(view);
  current = VIEWS[route.name](view, hub, route);
  for (const item of nav.querySelectorAll('a[data-route]'))
    item.setAttribute('aria-current', item.dataset.route === route.name ? 'page' : 'false');
  closeNav();
  window.scrollTo(0, 0);
}

function refreshShell() {
  const links = [...hub.links.values()];
  const connected = links.filter((link) => link.connected).length;
  const reconnecting = links.some((link) => link.state === 'waiting' || link.state === 'reconnecting');
  setStatus(connected > 0 ? 'connected' : 'connecting', `${connected}/${links.length} machine${links.length === 1 ? '' : 's'} connected${reconnecting ? ' · reconnecting…' : ''}`);
  setBadge('pending-count', hub.pending().length);
  setBadge('unread-count', hub.unreadCount());
  current?.update?.();
}

function setBadge(id, count) {
  const badge = document.getElementById(id);
  badge.textContent = String(count);
  badge.hidden = count === 0;
}

function closeNav() {
  nav.classList.remove('open');
  navToggle.setAttribute('aria-expanded', 'false');
}

function readTheme() {
  try {
    return localStorage.getItem(THEME_KEY) ?? 'auto';
  } catch {
    return 'auto';
  }
}

function applyTheme(theme) {
  if (theme === 'auto')
    delete document.documentElement.dataset.theme;
  else
    document.documentElement.dataset.theme = theme;
  const button = document.getElementById('theme-toggle');
  button.textContent = theme === 'dark' ? '☾' : theme === 'light' ? '☀' : '◐';
  button.title = `Theme: ${theme}`;
  try {
    localStorage.setItem(THEME_KEY, theme);
  } catch {
    // Storage may be unavailable (private mode); the theme then lasts for this page only.
  }
}

function showLogin(message) {
  setStatus('login', 'Log in to continue.');
  shell.hidden = true;
  loginError.hidden = message === undefined;
  loginError.textContent = message ?? '';
  loginForm.hidden = false;
}

async function startHub() {
  loginForm.hidden = true;
  shell.hidden = false;
  setStatus('connecting', 'Connecting…');
  hub = new Hub();
  hub.addEventListener('change', refreshShell);
  hub.addEventListener('unauthorized', () => showLogin('The bridge restarted or the session expired; log in again.'));
  await hub.start();
  window.addEventListener('hashchange', render);
  render();
  refreshShell();
}

loginForm.addEventListener('submit', async (event) => {
  event.preventDefault();
  if (!(await login(document.getElementById('token').value))) {
    showLogin('The token was not accepted.');
    return;
  }
  await run(startHub);
});

navToggle.addEventListener('click', () => {
  const open = nav.classList.toggle('open');
  navToggle.setAttribute('aria-expanded', String(open));
});

document.getElementById('theme-toggle').addEventListener('click', () => {
  applyTheme(THEMES[(THEMES.indexOf(readTheme()) + 1) % THEMES.length]);
});

async function run(step) {
  try {
    await step();
  } catch (error) {
    setStatus('error', `Failed: ${error?.message ?? String(error)}`);
    mount(view, h('p', { className: 'error' }, error?.message ?? String(error)));
  }
}

async function start() {
  applyTheme(readTheme());
  const fragmentToken = takeFragmentToken();
  if (fragmentToken !== null && !(await login(fragmentToken))) {
    showLogin('The token in the link was not accepted.');
    return;
  }
  if (!(await hasSession())) {
    showLogin();
    return;
  }
  await startHub();
}

run(start);
