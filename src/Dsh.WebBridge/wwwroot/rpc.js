// JSON-RPC 2.0 client for the dsharp web bridge.
// The bridge relays every WebSocket text message verbatim to the host (one message is one JSON-RPC request, response or notification), so this module is the whole client-side protocol layer.
// Method names and parameter shapes are the host's (Dsh.RemoteHost HostProtocol); parameter and result property names are PascalCase and case-sensitive.

/** JSON-RPC "method not found", returned for requests the host sends to the browser. */
const METHOD_NOT_FOUND = -32601;

/** A JSON-RPC error response from the host. */
export class RpcError extends Error {
  constructor(error) {
    super(error.message);
    this.code = error.code;
    this.data = error.data;
  }
}

/** The bridge endpoint of the page's own origin; the session cookie set by /api/login authenticates it. */
export function bridgeUrl() {
  const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
  return `${scheme}//${location.host}/ws`;
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

  /** Sends a request and resolves with its result, or rejects with RpcError. */
  request(method, params) {
    const id = this.#nextId++;
    const message = params === undefined ? { jsonrpc: '2.0', id, method } : { jsonrpc: '2.0', id, method, params };
    return new Promise((resolve, reject) => {
      this.#pending.set(id, { resolve, reject });
      this.#socket.send(JSON.stringify(message));
    });
  }

  notify(method, params) {
    this.#socket.send(JSON.stringify(params === undefined ? { jsonrpc: '2.0', method } : { jsonrpc: '2.0', method, params }));
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

  close() {
    this.#socket.close(1000);
  }

  #dispatch(message) {
    if (message.method === undefined) {
      this.#settle(message);
      return;
    }
    if (message.id !== undefined) {
      this.#socket.send(JSON.stringify({ jsonrpc: '2.0', id: message.id, error: { code: METHOD_NOT_FOUND, message: `method not found: ${message.method}` } }));
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
