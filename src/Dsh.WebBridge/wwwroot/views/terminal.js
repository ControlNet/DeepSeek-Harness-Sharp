// Terminal of a terminal or external-agent session: xterm.js fed by host.pty.output and writing through host.pty.write.
// On touch screens a key bar supplies the keys a phone keyboard lacks (Esc, Tab, Ctrl, arrows).
import { Terminal } from '../vendor/xterm-6.0.0/xterm.mjs';
import { FitAddon } from '../vendor/addon-fit-0.11.0/addon-fit.mjs';
import { h } from '../dom.js';
import { describe } from '../hub.js';

const ESC = '\x1b';

/** Ctrl+<letter> is the letter's code minus 64 (Ctrl+C = 0x03). */
const CONTROL_OFFSET = 64;

/** Resize requests are sent once the layout has settled, not for every intermediate size. */
const RESIZE_DEBOUNCE_MS = 150;

const NARROW_SCREEN = '(max-width: 700px)';

const THEME = {
  background: '#101418',
  foreground: '#e3e6ea',
  cursor: '#e3e6ea',
  selectionBackground: '#3a4a5c',
};

/** Arrow keys send SS3 sequences while the application asks for application cursor mode (vim, less), CSI otherwise. */
function arrow(term, letter) {
  return term.modes.applicationCursorKeysMode ? `${ESC}O${letter}` : `${ESC}[${letter}`;
}

const KEYS = [
  { label: 'Esc', send: () => ESC },
  { label: 'Tab', send: () => '\t' },
  { label: 'Ctrl', modifier: true },
  { label: '↑', name: 'Up', send: (term) => arrow(term, 'A') },
  { label: '↓', name: 'Down', send: (term) => arrow(term, 'B') },
  { label: '←', name: 'Left', send: (term) => arrow(term, 'D') },
  { label: '→', name: 'Right', send: (term) => arrow(term, 'C') },
  { label: '^C', name: 'Ctrl+C', send: () => '\x03' },
];

export function terminalPanel(machineLink, ptyId) {
  const screen = h('div', { className: 'terminal-screen', id: 'terminal-screen', dataset: { ptyId } });
  const state = h('p', { className: 'terminal-state muted', id: 'terminal-state' }, 'Attaching…');
  const term = new Terminal({
    cursorBlink: true,
    fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Consolas, "Liberation Mono", monospace',
    fontSize: matchMedia(NARROW_SCREEN).matches ? 12 : 14,
    scrollback: 5000,
    theme: THEME,
  });
  const fit = new FitAddon();
  let ctrlArmed = false;
  const ctrlButton = h('button', { type: 'button', className: 'key', 'aria-pressed': 'false' }, 'Ctrl');

  const send = (data) => machineLink.writeTerminal(ptyId, data).catch((error) => setState(`Input not delivered: ${describe(error)}`));
  const setState = (text) => {
    state.textContent = text;
    state.hidden = text.length === 0;
  };

  /** A one-shot Ctrl from the key bar turns the next typed character into its control code. */
  term.onData((data) => {
    if (ctrlArmed && data.length === 1 && /[@-_a-z]/i.test(data)) {
      data = String.fromCharCode(data.toUpperCase().charCodeAt(0) - CONTROL_OFFSET);
      armCtrl(false);
    }
    send(data);
  });

  function armCtrl(armed) {
    ctrlArmed = armed;
    ctrlButton.setAttribute('aria-pressed', String(armed));
  }

  const keyBar = h('div', { className: 'key-bar', role: 'toolbar', 'aria-label': 'Terminal keys' }, KEYS.map((key) => {
    const button = key.modifier ? ctrlButton : h('button', { type: 'button', className: 'key', 'aria-label': key.name ?? key.label }, key.label);
    // Pressing a key must not move focus away from the terminal, or the phone keyboard closes.
    button.addEventListener('pointerdown', (event) => event.preventDefault());
    button.addEventListener('click', () => {
      if (key.modifier)
        armCtrl(!ctrlArmed);
      else
        send(key.send(term));
      term.focus();
    });
    return button;
  }));

  let resizeTimer = null;
  term.onResize(({ cols, rows }) => {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(() => machineLink.resizeTerminal(ptyId, cols, rows).catch(() => {}), RESIZE_DEBOUNCE_MS);
  });
  const observer = new ResizeObserver(() => fitSafely());

  function fitSafely() {
    if (screen.clientWidth > 0 && screen.clientHeight > 0)
      fit.fit();
  }

  const detach = machineLink.attachTerminal(ptyId, {
    data: (bytes, snapshot) => {
      if (snapshot)
        term.reset();
      term.write(bytes);
      setState('');
    },
    eof: () => setState('The process has ended.'),
    error: (text) => setState(`Cannot attach: ${text}`),
  });

  const element = h('div', { className: 'terminal-panel' }, screen, state, keyBar);
  return {
    element,
    /** Opens xterm once the panel is in the document, so it can measure its cells. */
    start() {
      term.loadAddon(fit);
      term.open(screen);
      fitSafely();
      observer.observe(screen);
      term.focus();
    },
    dispose() {
      observer.disconnect();
      clearTimeout(resizeTimer);
      detach();
      term.dispose();
    },
  };
}
