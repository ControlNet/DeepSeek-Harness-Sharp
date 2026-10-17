// JSON-RPC 2.0 client for the dsharp web bridge.
// Each WebSocket is a view transport over the bridge's shared Fleet client, not a raw host connection.
// Host method names remain the user-operation API; bridge.view contains the server's authoritative render projection.

import { JsonRpc } from '/protocol.js';

/** Error code of requests made while the WebSocket is not open (the WebSocket close code of normal closure). */
const CONNECTION_CLOSED = 1000;

/** A JSON-RPC error response from the host. */
export class RpcError extends Error {
  constructor(error) {
    super(error.message);
    this.code = error.code;
    this.data = error.data;
  }
}

/** The bridge endpoint of the page's own origin, optionally for one machine of the bridge's fleet; the session cookie set by /api/login authenticates it. */
export function bridgeUrl(machine) {
  const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
  const query = machine === undefined ? '' : `?machine=${encodeURIComponent(machine)}`;
  return `${scheme}//${location.host}/ws${query}`;
}

export class HostRpc {
  #socket;
  #nextId = 1;
  #pending = new Map();
  #handlers = new Map();
  #closeHandlers = [];

  constructor(socket) {
    this.#socket = socket;
    socket.addEventListener('message', (event) => this.#dispatch(JSON.parse(event.data)));
    socket.addEventListener('close', (event) => this.#closed(event));
  }

  /** Opens a WebSocket to the bridge; rejects with the close event when the bridge refuses or the host is unreachable. */
  static connect(url = bridgeUrl()) {
    return new Promise((resolve, reject) => {
      const socket = new WebSocket(url);
      socket.addEventListener('open', () => resolve(new HostRpc(socket)), { once: true });
      socket.addEventListener('close', (event) => reject(event), { once: true });
    });
  }

  /** Sends a request and resolves with its result, or rejects with RpcError; a socket that is no longer open rejects at once instead of dropping the message. */
  request(method, params) {
    if (this.#socket.readyState !== WebSocket.OPEN)
      return Promise.reject(new RpcError({ code: CONNECTION_CLOSED, message: 'not connected' }));
    const id = this.#nextId++;
    const message = params === undefined ? { jsonrpc: JsonRpc.Version, id, method } : { jsonrpc: JsonRpc.Version, id, method, params };
    return new Promise((resolve, reject) => {
      this.#pending.set(id, { resolve, reject });
      this.#socket.send(JSON.stringify(message));
    });
  }

  notify(method, params) {
    this.#socket.send(JSON.stringify(params === undefined ? { jsonrpc: JsonRpc.Version, method } : { jsonrpc: JsonRpc.Version, method, params }));
  }

  /** Subscribes to a host notification such as host.event or host.approval; returns the unsubscribe function. */
  on(method, handler) {
    const handlers = this.#handlers.get(method) ?? new Set();
    handlers.add(handler);
    this.#handlers.set(method, handlers);
    return () => handlers.delete(handler);
  }

  /** The handler receives the WebSocket close event; code and reason say why (for example 1001 when the host closed the connection). */
  onClose(handler) {
    this.#closeHandlers.push(handler);
  }

  get isOpen() {
    return this.#socket.readyState === WebSocket.OPEN;
  }

  close() {
    this.#socket.close(CONNECTION_CLOSED);
  }

  #dispatch(message) {
    if (message.method === undefined) {
      this.#settle(message);
      return;
    }
    if (message.id !== undefined) {
      this.#socket.send(JSON.stringify({ jsonrpc: JsonRpc.Version, id: message.id, error: { code: JsonRpc.MethodNotFound, message: `method not found: ${message.method}` } }));
      return;
    }
    for (const handler of this.#handlers.get(message.method) ?? [])
      handler(message.params);
  }

  #settle(message) {
    const pending = this.#pending.get(message.id);
    if (pending === undefined)
      return;
    this.#pending.delete(message.id);
    if (message.error !== undefined)
      pending.reject(new RpcError(message.error));
    else
      pending.resolve(message.result);
  }

  #closed(event) {
    for (const pending of this.#pending.values())
      pending.reject(new RpcError({ code: event.code, message: event.reason || 'connection closed' }));
    this.#pending.clear();
    for (const handler of this.#closeHandlers)
      handler(event);
  }
}
