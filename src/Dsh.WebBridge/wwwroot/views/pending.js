// Pending decisions: approvals and questions of every machine, answerable in place.
// The first answer from any client wins; cards disappear when host.pending.resolved arrives, whoever answered.
import { h, mount, link, toast } from '../dom.js';
import { describe } from '../hub.js';
import { sessionRoute, sessionTitle, emptyState, syncList, prettyJson } from './common.js';

export function pendingView(container, hub) {
  const list = h('div', { className: 'pending-list', id: 'pending-list' });
  const empty = emptyState('Nothing is waiting for an answer.');
  mount(container, h('h1', {}, 'Pending decisions'), empty, list);

  function update() {
    const items = hub.pending();
    empty.hidden = items.length > 0;
    syncPendingCards(list, hub, items);
  }

  update();
  return { update };
}

/** Keeps one card per pending request; a card being filled in is never rebuilt. */
export function syncPendingCards(list, hub, items) {
  syncList(list, items, (item) => `${item.machine}/${item.request.RequestId}`, (item) => pendingCard(hub, item));
}

export function pendingCard(hub, item) {
  const machineLink = hub.link(item.machine);
  const session = machineLink?.sessions.get(item.request.SessionId);
  const head = h('header', { className: 'pending-head' },
    h('span', { className: `chip kind-${item.kind}` }, item.kind === 'approval' ? 'Approval' : 'Question'),
    h('span', { className: 'muted' }, item.machine, ' · '),
    link(sessionRoute(item.machine, item.request.SessionId), {}, session ? sessionTitle(session) : item.request.SessionId));
  const body = item.kind === 'approval' ? approvalBody(machineLink, item.request) : questionBody(machineLink, item.request);
  return h('article', { className: `pending card ${item.kind}`, dataset: { request: item.request.RequestId, kind: item.kind } }, head, body);
}

function approvalBody(machineLink, request) {
  const allow = h('button', { type: 'button', className: 'primary', onClick: () => answer(true) }, 'Allow');
  const reject = h('button', { type: 'button', className: 'danger', onClick: () => answer(false) }, 'Reject');

  async function answer(allowed) {
    allow.disabled = reject.disabled = true;
    try {
      const applied = await machineLink.request('host.approval.respond', { RequestId: request.RequestId, Allow: allowed, Reason: null });
      if (!applied)
        toast('Someone else already answered this request.');
    } catch (error) {
      toast(`Could not answer: ${describe(error)}`, 'error');
      allow.disabled = reject.disabled = false;
    }
  }

  const argumentsText = prettyJson(request.Arguments);
  return h('div', { className: 'pending-body' },
    h('p', {}, h('span', { className: 'muted' }, 'Tool '), h('code', {}, request.Tool)),
    request.Reason ? h('p', { className: 'reason' }, request.Reason) : null,
    argumentsText ? h('pre', { className: 'arguments' }, argumentsText) : null,
    h('div', { className: 'actions' }, allow, reject));
}

function questionBody(machineLink, request) {
  const fields = request.Questions.map((question) => questionField(request.RequestId, question));
  const submit = h('button', { type: 'submit', className: 'primary' }, request.Questions.find((question) => question.ApproveLabel)?.ApproveLabel ?? 'Submit');
  const form = h('form', {
    className: 'pending-body',
    onSubmit: async (event) => {
      event.preventDefault();
      submit.disabled = true;
      try {
        const applied = await machineLink.request('host.question.respond', { RequestId: request.RequestId, Answers: fields.map((field) => field.answer()) });
        if (!applied)
          toast('Someone else already answered this question.');
      } catch (error) {
        toast(`Could not answer: ${describe(error)}`, 'error');
        submit.disabled = false;
      }
    },
  }, fields.map((field) => field.element), h('div', { className: 'actions' }, submit));
  return form;
}

/** One question: its options as radio buttons or checkboxes, plus a free-text answer. */
function questionField(requestId, question) {
  const name = `q-${requestId}-${question.Id}`;
  const options = question.Options.map((option, index) => {
    const input = h('input', { type: question.MultiSelect ? 'checkbox' : 'radio', name, value: option.Label, id: `${name}-${index}` });
    return {
      input,
      element: h('label', { className: 'option', for: `${name}-${index}` }, input,
        h('span', {}, option.Label, option.Description ? h('span', { className: 'muted block' }, option.Description) : null)),
    };
  });
  const custom = h('input', { type: 'text', placeholder: 'Other answer (optional)', 'aria-label': 'Other answer' });
  const element = h('fieldset', { className: 'question' },
    h('legend', {}, question.Header ?? question.Question),
    question.Header ? h('p', {}, question.Question) : null,
    question.Detail ? h('p', { className: 'muted' }, question.Detail) : null,
    options.map((option) => option.element),
    custom);
  return {
    element,
    answer: () => ({
      Id: question.Id,
      Selected: options.filter((option) => option.input.checked).map((option) => option.input.value),
      Custom: custom.value.trim() || null,
    }),
  };
}
