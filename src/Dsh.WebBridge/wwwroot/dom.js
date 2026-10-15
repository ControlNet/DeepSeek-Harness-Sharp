// DOM construction for every view.
// Host data (titles, messages, file contents) can contain markup, so it only ever becomes text nodes: nothing here assigns innerHTML.

/**
 * Creates an element: props sets properties (className, type, disabled, ...), data-* and aria-* attributes, and on<Event> listeners.
 * Children may be nodes, strings (as text nodes), arrays, or null/false (skipped).
 */
export function h(tag, props = {}, ...children) {
  const element = document.createElement(tag);
  for (const [key, value] of Object.entries(props ?? {})) {
    if (value === undefined || value === null || value === false)
      continue;
    if (key.startsWith('on') && typeof value === 'function')
      element.addEventListener(key.slice(2).toLowerCase(), value);
    else if (key === 'dataset')
      Object.assign(element.dataset, value);
    else if (key.startsWith('aria-') || key === 'role' || key === 'for' || key === 'list')
      element.setAttribute(key, String(value));
    else
      element[key] = value;
  }
  append(element, children);
  return element;
}

function append(parent, children) {
  for (const child of children) {
    if (child === null || child === undefined || child === false)
      continue;
    if (Array.isArray(child))
      append(parent, child);
    else
      parent.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
}

/** Replaces all children of a node. */
export function mount(parent, ...children) {
  parent.replaceChildren();
  append(parent, children);
  return parent;
}

/** A link to a hash route of this page. */
export function link(route, props, ...children) {
  return h('a', { ...props, href: `#${route}` }, ...children);
}

const relative = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });

const RELATIVE_STEPS = [[60, 'second'], [60, 'minute'], [24, 'hour'], [7, 'day'], [Number.POSITIVE_INFINITY, 'week']];

/** "3 minutes ago" for a Unix-millisecond or ISO time. */
export function ago(time) {
  const millis = typeof time === 'number' ? time : Date.parse(time);
  if (!Number.isFinite(millis) || millis <= 0)
    return '';
  let value = (millis - Date.now()) / 1000;
  for (const [size, unit] of RELATIVE_STEPS) {
    if (Math.abs(value) < size)
      return relative.format(Math.round(value), unit);
    value /= size;
  }
  return '';
}

/** Full local date and time, for tooltips. */
export function when(time) {
  const millis = typeof time === 'number' ? time : Date.parse(time);
  return Number.isFinite(millis) ? new Date(millis).toLocaleString() : '';
}

const encoder = new TextEncoder();
const decoder = new TextDecoder();

/** UTF-8 text to base64, as host.pty.write expects. */
export function textToBase64(text) {
  return bytesToBase64(encoder.encode(text));
}

export function bytesToBase64(bytes) {
  let binary = '';
  for (let index = 0; index < bytes.length; index++)
    binary += String.fromCharCode(bytes[index]);
  return btoa(binary);
}

export function base64ToBytes(base64) {
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index++)
    bytes[index] = binary.charCodeAt(index);
  return bytes;
}

/** Decodes UTF-8; invalid sequences become U+FFFD instead of failing. */
export function bytesToText(bytes) {
  return decoder.decode(bytes);
}

/** The last path segment, for both / and \ separators. */
export function baseName(path) {
  const trimmed = (path ?? '').replace(/[\\/]+$/, '');
  const cut = Math.max(trimmed.lastIndexOf('/'), trimmed.lastIndexOf('\\'));
  return cut >= 0 ? trimmed.slice(cut + 1) || trimmed : trimmed;
}

/** A short-lived message at the bottom of the screen. */
export function toast(message, kind = 'info') {
  const host = document.getElementById('toasts');
  const item = h('div', { className: `toast toast-${kind}`, role: 'status' }, message);
  host.append(item);
  setTimeout(() => item.remove(), TOAST_MILLIS);
}

const TOAST_MILLIS = 4000;
