const form = document.querySelector('#filters');
const search = document.querySelector('#search');
const range = document.querySelector('#range');
const hostFilter = document.querySelector('#host');
const appFilter = document.querySelector('#app');
const facilityFilter = document.querySelector('#facility');
const severityFilter = document.querySelector('#severity');
const list = document.querySelector('#logs');
const empty = document.querySelector('#empty');
const older = document.querySelector('#older');
const liveButton = document.querySelector('#live');
const notice = document.querySelector('#notice');
const template = document.querySelector('#row');
let cursor = null;
let source = null;

const severityNames = ['emerg', 'alert', 'crit', 'err', 'warning', 'notice', 'info', 'debug'];
const facilityNames = [
  'kernel', 'user', 'mail', 'daemon', 'auth', 'syslog', 'lpr', 'news',
  'uucp', 'clock', 'authpriv', 'ftp', 'ntp', 'audit', 'alert', 'clock2',
  'local0', 'local1', 'local2', 'local3', 'local4', 'local5', 'local6', 'local7'
];

facilityNames.forEach((name, number) => {
  const option = document.createElement('option');
  option.value = number;
  option.textContent = `${number} · ${name}`;
  facilityFilter.append(option);
});

function parameters() {
  const params = new URLSearchParams();
  if (search.value) params.set('q', search.value);
  if (range.value) params.set('range', range.value);
  if (hostFilter.value) params.set('host', hostFilter.value);
  if (appFilter.value) params.set('app', appFilter.value);
  if (facilityFilter.value) params.set('facility', facilityFilter.value);
  if (severityFilter.value) params.set('severity', severityFilter.value);
  return params;
}

function render(item, prepend = false) {
  if (document.querySelector(`[data-id="${item.id}"]`)) return;
  const row = template.content.firstElementChild.cloneNode(true);
  row.dataset.id = item.id;
  row.querySelector('time').textContent = new Date(item.receivedAt).toLocaleString();
  const host = row.querySelector('.host');
  host.textContent = item.hostname || item.sourceAddress;
  host.disabled = !item.hostname;
  host.onclick = () => { hostFilter.value = item.hostname; refresh(); };
  const app = row.querySelector('.app');
  app.textContent = item.application
    ? `${item.application}${item.processId ? `[${item.processId}]` : ''}`
    : '—';
  app.disabled = !item.application;
  app.onclick = () => { appFilter.value = item.application; refresh(); };
  const facility = row.querySelector('.facility');
  facility.textContent = Number.isInteger(item.facility) ? item.facility : '—';
  facility.disabled = !Number.isInteger(item.facility);
  facility.title = Number.isInteger(item.facility) ? facilityNames[item.facility] : '';
  facility.onclick = () => { facilityFilter.value = item.facility; refresh(); };
  const severity = row.querySelector('.severity');
  severity.textContent = severityNames[item.severity] || '—';
  severity.disabled = !Number.isInteger(item.severity);
  severity.onclick = () => { severityFilter.value = item.severity; refresh(); };
  row.querySelector('.message').textContent = item.message;
  row.title = `source=${item.sourceAddress}\nraw=${item.rawMessage}`;
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

async function showLosses() {
  const response = await fetch('/api/status');
  if (!response.ok) return;
  const status = await response.json();
  const dropped = status.floodDropped + status.queueDropped + status.storageFailed;
  notice.textContent = dropped
    ? `${dropped} messages lost since service start; inspect /api/status.`
    : '';
}

function connect() {
  if (source) source.close();
  source = new EventSource(`/api/tail?${parameters()}`);
  source.onmessage = event => {
    render(JSON.parse(event.data), true);
    empty.style.display = 'none';
  };
  source.onerror = () => { notice.textContent = 'Live connection interrupted; reconnecting.'; };
  source.onopen = () => { showLosses(); };
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
hostFilter.value = initial.get('host') || '';
appFilter.value = initial.get('app') || '';
facilityFilter.value = initial.get('facility') || '';
severityFilter.value = initial.get('severity') || '';
load().catch(error => { notice.textContent = error.message; });
showLosses();
connect();
