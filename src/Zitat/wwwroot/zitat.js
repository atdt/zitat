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
let source = null;

const severityNames = ['emerg', 'alert', 'crit', 'err', 'warning', 'notice', 'info', 'debug'];

function parameters() {
  const params = new URLSearchParams();
  if (search.value) params.set('q', search.value);
  if (range.value) params.set('range', range.value);
  return params;
}

function addFilter(name, value) {
  const token = `${name}:${value}`;
  const tokens = search.value.split(/\s+/).filter(Boolean)
    .filter(item => !item.startsWith(`${name}:`));
  search.value = [token, ...tokens].join(' ');
  refresh();
}

function render(item, prepend = false) {
  if (document.querySelector(`[data-id="${item.id}"]`)) return;
  const row = template.content.firstElementChild.cloneNode(true);
  row.dataset.id = item.id;
  row.querySelector('time').textContent = new Date(item.receivedAt).toLocaleString();
  const host = row.querySelector('.host');
  host.textContent = item.hostname || item.sourceAddress;
  host.onclick = () => addFilter('host', item.hostname || item.sourceAddress);
  const app = row.querySelector('.app');
  app.textContent = item.application || '—';
  app.disabled = !item.application;
  app.onclick = () => addFilter('app', item.application);
  row.querySelector('.severity').textContent = severityNames[item.severity] || '—';
  row.querySelector('.message').textContent = item.message;
  prepend ? list.prepend(row) : list.append(row);
}

async function load(append = false) {
  const params = parameters();
  if (append && cursor) params.set('before', cursor);
  const response = await fetch(`/api/logs?${params}`);
  if (!response.ok) throw new Error(`Search failed: HTTP ${response.status}`);
  const page = await response.json();
  if (!append) list.replaceChildren();
  page.items.forEach(item => render(item));
  cursor = page.nextBeforeId;
  older.hidden = !cursor || page.items.length === 0;
  empty.style.display = list.children.length ? 'none' : 'block';
}

function connect() {
  if (source) source.close();
  source = new EventSource(`/api/tail?${parameters()}`);
  source.onmessage = event => {
    render(JSON.parse(event.data), true);
    empty.style.display = 'none';
  };
  source.onerror = () => { notice.textContent = 'Live connection interrupted; reconnecting.'; };
  source.onopen = () => { notice.textContent = ''; };
  liveButton.textContent = 'Pause';
}

function refresh() {
  const params = parameters();
  history.replaceState(null, '', params.size ? `?${params}` : '/');
  load().catch(error => { notice.textContent = error.message; });
  connect();
}

form.onsubmit = event => { event.preventDefault(); refresh(); };
older.onclick = () => load(true).catch(error => { notice.textContent = error.message; });
liveButton.onclick = () => {
  if (source) { source.close(); source = null; liveButton.textContent = 'Resume'; }
  else connect();
};

const initial = new URLSearchParams(location.search);
search.value = initial.get('q') || '';
range.value = initial.get('range') ?? '1h';
load().catch(error => { notice.textContent = error.message; });
connect();
