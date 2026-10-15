// Minimal page proving the browser -> bridge -> host path: log in, open the WebSocket, negotiate with host.hello and show host.info.
import { HostRpc } from './rpc.js';

/** Must equal Dsh.RemoteHost HostProtocol.Version; host.hello reports a mismatch otherwise. */
const PROTOCOL_VERSION = 1;

const status = document.getElementById('status');
const loginForm = document.getElementById('login');
const loginError = document.getElementById('login-error');

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
    history.replaceState(null, '', `${location.pathname}${location.search}`);
  return token;
}

function showInfo(info) {
  const list = document.getElementById('info');
  list.replaceChildren();
  for (const [key, value] of Object.entries(info)) {
    const term = document.createElement('dt');
    term.textContent = key;
    const detail = document.createElement('dd');
    detail.textContent = String(value);
    list.append(term, detail);
  }
  document.getElementById('raw').textContent = JSON.stringify(info, null, 2);
  document.getElementById('host').hidden = false;
}

async function connect() {
  setStatus('connecting', 'Connecting to the host…');
  const rpc = await HostRpc.connect();
  rpc.onClose((event) => setStatus('error', `Disconnected (${event.code}${event.reason ? `: ${event.reason}` : ''})`));
  const hello = await rpc.request('host.hello', { ProtocolVersion: PROTOCOL_VERSION });
  if (!hello.Ok)
    throw new Error(`host refused the connection: ${hello.Mismatch}`);
  showInfo(await rpc.request('host.info'));
  setStatus('connected', `Connected to host ${hello.HostVersion}`);
}

function showLogin(message) {
  setStatus('login', 'Log in to continue.');
  loginError.hidden = message === undefined;
  loginError.textContent = message ?? '';
  loginForm.hidden = false;
}

loginForm.addEventListener('submit', async (event) => {
  event.preventDefault();
  if (!(await login(document.getElementById('token').value))) {
    showLogin('The token was not accepted.');
    return;
  }
  loginForm.hidden = true;
  await run(connect);
});

async function run(step) {
  try {
    await step();
  } catch (error) {
    const reason = error instanceof Event ? `bridge closed the WebSocket (${error.code ?? ''} ${error.reason ?? ''})` : error.message;
    setStatus('error', `Failed: ${reason}`);
  }
}

async function start() {
  const fragmentToken = takeFragmentToken();
  if (fragmentToken !== null && !(await login(fragmentToken))) {
    showLogin('The token in the link was not accepted.');
    return;
  }
  if (!(await hasSession())) {
    showLogin();
    return;
  }
  await connect();
}

run(start);
