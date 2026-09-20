const form = document.querySelector('#filters');
const search = document.querySelector('#search');
const range = document.querySelector('#range');
const list = document.querySelector('#logs');
const empty = document.querySelector('#empty');
const older = document.querySelector('#older');
const liveButton = document.querySelector('#live');
const notice = document.querySelector('#notice');
const template = document.querySelector('#row');
let cursor = null;
let stream = null;
let liveAfter = null;
let liveSince = null;
let requestId = 0;

const severityNames = ['emerg', 'alert', 'crit', 'err', 'warning', 'notice', 'info', 'debug'];
const facilityNames = [
  'kernel', 'user', 'mail', 'daemon', 'auth', 'syslog', 'lpr', 'news',
  'uucp', 'clock', 'authpriv', 'ftp', 'ntp', 'audit', 'alert', 'clock2',
  'local0', 'local1', 'local2', 'local3', 'local4', 'local5', 'local6', 'local7'
];

function parameters() {
  const params = new URLSearchParams();
  if (search.value) params.set('q', search.value);
  if (range.value) params.set('range', range.value);
  return params;
}

function appendFilter(name, value) {
  search.value = `${search.value.trim()} ${name}:${value}`.trim();
  refresh();
}

function quote(value) {
  return /\s/.test(value) ? `"${value}"` : value;
}

function field(row, selector, text, filter, value) {
  const button = row.querySelector(selector);
  button.textContent = text ?? '—';
  button.disabled = value === null || value === undefined;
  if (!button.disabled) button.onclick = () => appendFilter(filter, quote(value));
  return button;
}

function render(item, prepend = false) {
  if (document.querySelector(`[data-cursor="${item.cursor}"]`)) return;
  const row = template.content.firstElementChild.cloneNode(true);
  row.dataset.cursor = item.cursor;
  row.querySelector('time').textContent = new Date(item.realtime).toLocaleString();

  field(row, '.host', item.hostname ?? item.source, 'host', item.hostname);
  field(
    row,
    '.app',
    item.application ? `${item.application}${item.processId ? `[${item.processId}]` : ''}` : null,
    'app',
    item.application
  );
  field(row, '.unit', item.unit, 'unit', item.unit);
  field(
    row,
    '.facility',
    Number.isInteger(item.facility) ? facilityNames[item.facility] : null,
    'facility',
    Number.isInteger(item.facility) ? facilityNames[item.facility] : null
  );
  field(
    row,
    '.severity',
    Number.isInteger(item.severity) ? severityNames[item.severity] : null,
    'severity',
    Number.isInteger(item.severity) ? severityNames[item.severity] : null
  );

  row.querySelector('.message').textContent = item.message;
  // The row shows selected fields; its tooltip exposes all journal fields.
  row.title = item.fields.map(([name, value]) => `${name}=${value}`).join('\n');
  prepend ? list.prepend(row) : list.append(row);
}

async function load(append = false) {
  const id = requestId;
  const params = parameters();
  if (append && cursor) params.set('before', cursor);
  const response = await fetch(`/api/logs?${params}`);
  if (!response.ok) throw new Error(`Search failed: HTTP ${response.status}`);
  const page = await response.json();
  if (id !== requestId) return null;
  if (!append) list.replaceChildren();
  page.items.forEach(item => render(item));
  cursor = page.nextBefore;
  if (!append) {
    liveAfter = page.liveAfter;
    liveSince = page.liveSince;
  }
  older.hidden = !cursor || page.items.length === 0;
  empty.style.display = list.children.length ? 'none' : 'block';
  return page;
}

async function showSummary() {
  const response = await fetch('/api/status');
  if (!response.ok) return;
  const { journal } = await response.json();
  const entries = journal.entries.toLocaleString();
  const senders = journal.sources.length;
  notice.textContent =
    `${entries} entries from ${senders} ${senders === 1 ? 'source' : 'sources'}` +
    ` in ${journal.files} journal files.`;
}

function connect() {
  if (stream) stream.close();
  const params = parameters();
  if (liveAfter) params.set('after', liveAfter);
  else if (liveSince) params.set('from', liveSince);
  stream = new EventSource(`/api/tail?${params}`);
  stream.onmessage = event => {
    liveAfter = event.lastEventId;
    render(JSON.parse(event.data), true);
    empty.style.display = 'none';
  };
  stream.onerror = () => { notice.textContent = 'Live connection interrupted; reconnecting.'; };
  stream.onopen = () => { showSummary(); };
  liveButton.textContent = 'Pause';
  liveButton.disabled = false;
}

async function refresh() {
  const id = ++requestId;
  if (stream) { stream.close(); stream = null; }
  liveButton.disabled = true;
  const params = parameters();
  history.replaceState(null, '', params.size ? `?${params}` : '/');
  try {
    const page = await load();
    if (id === requestId && page) connect();
  } catch (error) {
    if (id === requestId) notice.textContent = error.message;
  }
}

form.onsubmit = event => { event.preventDefault(); refresh(); };
older.onclick = () => load(true).catch(error => { notice.textContent = error.message; });
liveButton.onclick = () => {
  if (stream) { stream.close(); stream = null; liveButton.textContent = 'Resume'; }
  else connect();
};

const initial = new URLSearchParams(location.search);
search.value = initial.get('q') || '';
range.value = initial.get('range') ?? '1h';
showSummary();
refresh();
