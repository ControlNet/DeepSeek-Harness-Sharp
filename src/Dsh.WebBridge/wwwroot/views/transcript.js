// Renders a harness-shaped session event stream (harness sessions, and ACP sessions the host translates) as messages, tool calls and the plan.
// Events arrive once each in seq order (MachineLink deduplicates across reconnects); envelopes are lowercase {type, seq, time, data} with camelCase payloads.
import { h, mount } from '../dom.js';
import { prettyJson } from './common.js';

/** System messages from this plugin source carry an ACP agent's plan, one `[status] content` line per entry. */
const PLAN_SOURCE = 'acp-plan';

/** Scroll distance from the bottom within which new content keeps the view pinned to the end. */
const STICK_TO_BOTTOM_PX = 80;

export class Transcript {
  element = h('div', { className: 'transcript', id: 'transcript', 'aria-live': 'polite' });
  #streaming = new Map();
  #tools = new Map();
  #plan = null;

  reset() {
    this.#streaming.clear();
    this.#tools.clear();
    this.#plan = null;
    this.element.replaceChildren();
  }

  /** Applies one event; unknown types (request headers, step markers) are not shown. */
  apply(event) {
    const pinned = this.#isPinned();
    const handler = HANDLERS[event.type];
    handler?.call(this, event.data ?? {}, event);
    if (pinned)
      this.element.scrollTop = this.element.scrollHeight;
  }

  showError(text) {
    this.element.append(h('p', { className: 'error' }, text));
  }

  #isPinned() {
    return this.element.scrollHeight - this.element.scrollTop - this.element.clientHeight < STICK_TO_BOTTOM_PX;
  }

  #append(node, seq) {
    node.dataset.seq = String(seq);
    this.element.append(node);
    return node;
  }

  userMessage(data, event) {
    this.#append(bubble('user', 'You', messageText(data)), event.seq);
  }

  /** Streaming text of the current turn and step; the final assistant/message replaces it. */
  assistantChunk(data, event) {
    const chunk = data.chunk ?? {};
    if (chunk.type !== 'text-delta' && chunk.type !== 'reasoning-delta')
      return;
    const key = `${data.turn}:${data.step}`;
    let live = this.#streaming.get(key);
    if (live === undefined) {
      live = { node: this.#append(bubble('assistant streaming', 'Agent', ''), event.seq), text: '', thinking: '' };
      this.#streaming.set(key, live);
    }
    if (chunk.type === 'text-delta')
      live.text += chunk.text ?? '';
    else
      live.thinking += chunk.text ?? '';
    setBubbleText(live.node, live.text, live.thinking);
  }

  assistantMessage(data, event) {
    const key = `${data.turn}:${data.step}`;
    const text = messageText(data.message);
    const final = bubble(`assistant${data.interrupted ? ' interrupted' : ''}`, 'Agent', text);
    const live = this.#streaming.get(key);
    this.#streaming.delete(key);
    if (live !== undefined) {
      final.dataset.seq = String(event.seq);
      live.node.replaceWith(final);
    } else if (text.length > 0)
      this.#append(final, event.seq);
  }

  toolCall(data, event) {
    const status = h('span', { className: 'chip tool-running' }, 'running');
    const result = h('div', { className: 'tool-result' });
    const card = h('details', { className: 'tool card', open: false },
      h('summary', {}, h('span', { className: 'tool-name' }, data.name ?? 'tool'), status),
      h('pre', { className: 'arguments' }, prettyJson(data.arguments) || '{}'),
      result);
    this.#tools.set(data.callId, { card, status, result });
    this.#append(card, event.seq);
  }

  toolResult(data, event) {
    const message = data.message ?? {};
    const callId = message.block?.toolCallId ?? message.toolSource?.callId;
    const failed = Boolean(data.error) || Boolean(message.block?.isError);
    const text = toolResultText(message);
    const tool = this.#tools.get(callId);
    if (tool === undefined) {
      this.#append(h('pre', { className: `tool-orphan${failed ? ' failed' : ''}` }, text), event.seq);
      return;
    }
    tool.status.textContent = failed ? 'failed' : 'done';
    tool.status.className = `chip ${failed ? 'tool-failed' : 'tool-done'}`;
    mount(tool.result, h('pre', {}, text || '(no output)'));
  }

  /** The plan is shown once and updated in place, so the newest plan is always the visible one. */
  systemMessage(data, event) {
    const message = data.message ?? {};
    const text = messageText(message);
    if (message.source?.plugin !== PLAN_SOURCE) {
      this.#append(h('p', { className: 'system muted' }, text), event.seq);
      return;
    }
    const entries = text.split('\n').filter((line) => line.length > 0).map(planEntry);
    const card = h('section', { className: 'plan card', 'aria-label': 'Plan' },
      h('h3', {}, 'Plan'),
      h('ul', {}, entries.map(([status, content]) => h('li', { className: `plan-${status}` }, h('span', { className: 'plan-mark', 'aria-label': status }, PLAN_MARKS[status] ?? '•'), content))));
    if (this.#plan === null)
      this.#plan = this.#append(card, event.seq);
    else {
      this.#plan.replaceWith(card);
      this.#plan = card;
    }
  }

  turnEnd(data, event) {
    const reason = data.reason?.kind ?? 'completed';
    if (reason !== 'completed')
      this.#append(h('p', { className: 'turn-end muted' }, `Turn ended: ${reason}`), event.seq);
  }
}

const HANDLERS = {
  'user/message': Transcript.prototype.userMessage,
  'assistant/chunk': Transcript.prototype.assistantChunk,
  'assistant/message': Transcript.prototype.assistantMessage,
  'tool/call': Transcript.prototype.toolCall,
  'tool/result': Transcript.prototype.toolResult,
  'system/message': Transcript.prototype.systemMessage,
  'turn/end': Transcript.prototype.turnEnd,
};

const PLAN_MARKS = { completed: '✓', in_progress: '◐', pending: '○' };

function planEntry(line) {
  const match = /^\[([^\]]*)\]\s*(.*)$/.exec(line);
  return match ? [match[1], match[2]] : ['pending', line];
}

function bubble(kind, author, text) {
  const node = h('div', { className: `message ${kind}` }, h('span', { className: 'author' }, author), h('div', { className: 'text' }));
  setBubbleText(node, text, '');
  return node;
}

function setBubbleText(node, text, thinking) {
  mount(node.querySelector('.text'),
    thinking ? h('div', { className: 'thinking muted' }, thinking) : null,
    text);
}

/** Text blocks of a message; images and other blocks are named instead of shown. */
function messageText(message) {
  const content = message?.content;
  if (typeof content === 'string')
    return content;
  return (content ?? []).map((block) => {
    if (block.type === 'text')
      return block.text ?? '';
    if (block.type === 'image')
      return '[image]';
    return '';
  }).filter((text) => text.length > 0).join('\n');
}

function toolResultText(message) {
  const blocks = message.block?.content ?? message.content?.find((block) => block.type === 'tool-result')?.content ?? [];
  if (typeof blocks === 'string')
    return blocks;
  return blocks.map((block) => (block.type === 'text' ? block.text ?? '' : `[${block.type}]`)).join('\n');
}
