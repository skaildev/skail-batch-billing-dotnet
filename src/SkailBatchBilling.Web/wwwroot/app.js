const STAGE_LABELS = { Starting: 'Starting…', Issuing: 'Issuing in parallel', Completed: 'Completed', Failed: 'Failed' };
const INVOICE_LABELS = { Issuing: 'Issuing…', Authorized: 'Authorized', Rejected: 'Rejected', Pending: 'Pending' };
const KEY = 'batch-billing.batchId';

const $ = (id) => document.getElementById(id);
const els = {
  size: $('size'), start: $('start'), state: $('state'), counter: $('counter'), progress: $('progress'),
  customer: $('customer-inbox'), finance: $('finance-inbox'),
  error: $('error'), summary: $('summary'), invoices: $('invoices'), batchId: $('batchId'), monitor: $('monitor'),
};

let batch = null;
let source = null;

const money = (v) => Number(v).toLocaleString('en-US', { style: 'currency', currency: 'BRL' });
const isTerminal = (b) => b && ['Completed', 'Failed'].includes(b.phase);

function showError(message) {
  els.error.hidden = !message;
  els.error.textContent = message ?? '';
}

function render() {
  // size and button are locked while a batch is active (also between the click and the trigger answer)
  const active = batch?.active === true;
  els.start.disabled = active;
  els.size.disabled = active;
  if (batch) els.size.value = String(batch.size);

  const phase = batch?.phase;
  els.state.textContent = phase ? (STAGE_LABELS[phase] ?? phase) : 'Idle';
  els.state.className = 'badge ' + (phase === 'Failed' ? 'failed' : phase === 'Completed' ? 'done' : phase ? 'active' : 'idle');
  els.counter.textContent = batch ? `${batch.done}/${batch.size}` : '';
  els.progress.hidden = !batch;
  if (batch) { els.progress.max = batch.size; els.progress.value = batch.done; }
  els.batchId.textContent = batch?.batchId ?? '-';
  showError(batch?.error);

  const r = batch?.result;
  els.summary.hidden = !r;
  if (r) {
    $('sum-authorized').textContent = r.authorized;
    $('sum-rejected').textContent = r.rejected;
    $('sum-pending').textContent = r.pending;
    $('sum-total').textContent = money(r.amountBilled);
  }

  fillInbox(els.customer, $('customer-count'), batch?.customerInbox ?? [], (m) => [m.number, m.customer, m.fileName, money(m.amount)]);
  fillInbox(els.finance, $('finance-count'), batch?.financeInbox ?? [], (t) => [t.number, t.customer, t.status, t.reason ?? '']);

  els.invoices.replaceChildren(...(batch?.invoices ?? []).map((n) => {
    const row = document.createElement('tr');
    row.classList.toggle('retried', n.attempts > 1);
    const pill = document.createElement('span');
    pill.className = `pill ${n.state}`;
    pill.textContent = INVOICE_LABELS[n.state] ?? n.state;
    const status = document.createElement('td');
    status.append(pill);
    const cells = [n.number, n.customer, money(n.amount)].map((t) => { const c = document.createElement('td'); c.textContent = t; return c; });
    const attempts = document.createElement('td'); attempts.textContent = n.attempts;
    const details = document.createElement('td'); details.textContent = n.reason ?? n.invoiceNumber ?? '';
    row.append(...cells, status, attempts, details);
    return row;
  }));
}

function fillInbox(body, counter, items, columns) {
  counter.textContent = items.length;
  body.replaceChildren(...items.map((item) => {
    const row = document.createElement('tr');
    row.append(...columns(item).map((t) => { const c = document.createElement('td'); c.textContent = t; return c; }));
    return row;
  }));
}

function connect(batchId) {
  source?.close();
  source = new EventSource(`/api/batches/${batchId}/stream`);
  source.onmessage = (event) => {
    batch = JSON.parse(event.data);
    render();
    if (isTerminal(batch)) {
      source.close();
      localStorage.removeItem(KEY);
    }
  };
  // EventSource reconnects by itself; the stream sends the current snapshot first
}

async function start() {
  els.start.disabled = true;
  els.size.disabled = true;
  showError(null);
  try {
    const res = await fetch('/api/batches', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ size: Number(els.size.value) }),
    });
    if (!res.ok) throw new Error((await res.json().catch(() => ({}))).error ?? `HTTP ${res.status}`);
    batch = await res.json();
    localStorage.setItem(KEY, batch.batchId);
    render();
    connect(batch.batchId);
  } catch (e) {
    showError(`Could not start: ${e.message}`);
    render();
  }
}

els.start.addEventListener('click', start);

fetch('/api/config').then((r) => r.json()).then((c) => {
  if (c.monitorUrl) { els.monitor.href = c.monitorUrl; els.monitor.hidden = false; }
}).catch(() => {});

// reload keeps the table and the locked controls right: the state always comes from the server
const saved = localStorage.getItem(KEY);
if (saved) {
  fetch(`/api/batches/${saved}`).then((r) => (r.ok ? r.json() : null)).then((b) => {
    if (!b) return localStorage.removeItem(KEY);
    batch = b;
    render();
    if (!isTerminal(b)) connect(b.batchId);
  });
}
render();
