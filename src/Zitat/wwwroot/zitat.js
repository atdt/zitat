const form = document.querySelector("#filters");
const search = document.querySelector("#search");
const list = document.querySelector("#logs");
const empty = document.querySelector("#empty");
const older = document.querySelector("#older");
const liveButton = document.querySelector("#live");
const notice = document.querySelector("#notice");
const template = document.querySelector("#row");
const syntaxToggle = document.querySelector("#syntax-toggle");
const syntaxHelp = document.querySelector("#syntax-help");
const suggestions = document.querySelector("#suggestions");
const searchError = document.querySelector("#search-error");
const effectiveRange = document.querySelector("#effective-range");
let cursor = null;
let stream = null;
let liveAfter = null;
let liveSince = null;
let controller = null;

function formatTime(date) {
  const pad = (n, len = 2) => String(n).padStart(len, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${
    pad(date.getDate())
  } ` +
    `${pad(date.getHours())}:${pad(date.getMinutes())}:${
      pad(date.getSeconds())
    }.` +
    pad(date.getMilliseconds(), 3);
}

const severityNames = [
  "emerg",
  "alert",
  "crit",
  "err",
  "warning",
  "notice",
  "info",
  "debug",
];
const facilityNames = [
  "kernel",
  "user",
  "mail",
  "daemon",
  "auth",
  "syslog",
  "lpr",
  "news",
  "uucp",
  "clock",
  "authpriv",
  "ftp",
  "ntp",
  "audit",
  "alert",
  "clock2",
  "local0",
  "local1",
  "local2",
  "local3",
  "local4",
  "local5",
  "local6",
  "local7",
];

const operators = [
  ["host:", "Exact journal hostname"],
  ["app:", "Application identifier"],
  ["unit:", "Systemd unit"],
  ["source:", "Journal source"],
  ["boot:", "Boot ID"],
  ["facility:", "Facility name or number"],
  ["severity:", "Severity name, number, or comparison"],
  ["since:", "Inclusive start time"],
  ["until:", "Exclusive end time"],
];
const values = {
  severity: severityNames.map((name) => [name, "Exact severity"]),
  facility: facilityNames.map((name) => [name, "Journal facility"]),
  since: [["now-30m", "Last 30 minutes"], ["now-2h", "Last 2 hours"], ["now-7d", "Last 7 days"]],
  until: [["now", "Current time"], ["now-30m", "30 minutes ago"]],
};
let selectedSuggestion = 0;
let suggestionItems = [];

function closeSuggestions() {
  suggestions.hidden = true;
  suggestions.replaceChildren();
  suggestionItems = [];
  search.removeAttribute("aria-activedescendant");
}

function activeToken() {
  const before = search.value.slice(0, search.selectionStart);
  const found = before.search(/\S+$/);
  const start = found < 0 ? before.length : found;
  return { start, text: before.slice(start) };
}

function showSuggestions() {
  const { text } = activeToken();
  const colon = text.indexOf(":");
  const fieldName = colon < 0 ? null : text.slice(0, colon);
  const candidates = fieldName in values ? values[fieldName] :
    colon < 0 ? operators : [];
  const prefix = colon < 0 ? text : text.slice(colon + 1);
  suggestionItems = candidates
    .filter(([name]) => name.toLowerCase().startsWith(prefix.toLowerCase()))
    .filter(([name]) => !(fieldName in values && name === prefix))
    .slice(0, 8)
    .map(([name, description]) => ({
      label: name,
      description,
      fragment: fieldName in values ? `${fieldName}:${name}` : name,
    }));
  suggestions.replaceChildren();
  suggestionItems.forEach((item, index) => {
    const option = document.createElement("button");
    option.type = "button";
    option.id = `suggestion-${index}`;
    option.setAttribute("role", "option");
    const label = document.createElement("code");
    label.textContent = item.label;
    const detail = document.createElement("span");
    detail.textContent = item.description;
    option.append(label, detail);
    option.onmousedown = (event) => event.preventDefault();
    option.onclick = () => chooseSuggestion(index);
    suggestions.append(option);
  });
  selectedSuggestion = 0;
  suggestions.hidden = suggestionItems.length === 0 || !text;
  updateSelectedSuggestion();
}

function updateSelectedSuggestion() {
  for (const [index, option] of [...suggestions.children].entries()) {
    option.setAttribute("aria-selected", String(index === selectedSuggestion));
  }
  if (!suggestions.hidden) {
    search.setAttribute("aria-activedescendant", `suggestion-${selectedSuggestion}`);
  }
}

function chooseSuggestion(index) {
  const item = suggestionItems[index];
  if (!item) return;
  const { start } = activeToken();
  const end = search.selectionStart +
    (search.value.slice(search.selectionStart).match(/^\S*/)?.[0].length ?? 0);
  search.setRangeText(item.fragment, start, end, "end");
  search.focus();
  showSuggestions();
}

function showSearchError(message) {
  searchError.replaceChildren();
  const explanation = document.createElement("div");
  explanation.textContent = message;
  searchError.append(explanation);
  const selector = /^invalid (\w+):/.exec(message)?.[1];
  const match = selector && new RegExp(`(?:^|\\s)(${selector}:\\S*)`).exec(search.value);
  if (match) {
    const expression = document.createElement("code");
    expression.textContent = match[1];
    searchError.append(expression);
  }
  searchError.hidden = false;
  search.setAttribute("aria-invalid", "true");
}

function clearSearchError() {
  searchError.hidden = true;
  searchError.replaceChildren();
  search.removeAttribute("aria-invalid");
}

function parameters() {
  const params = new URLSearchParams();
  if (search.value) params.set("q", search.value);
  return params;
}

// Query.fs folds each selector into one field, so a repeated one silently
// wins over the earlier; replacing keeps the box honest about the query.
function appendFilter(name, value) {
  const existing = new RegExp(`(^|\\s)${name}:("[^"]*"|\\S*)`, "g");
  let replaced = false;
  const rest = search.value.replace(existing, (_, lead) => {
    if (replaced) return "";
    replaced = true;
    return `${lead}${name}:${value}`;
  });
  search.value = replaced ? rest.trim() : `${rest.trim()} ${name}:${value}`.trim();
  refresh();
}

function quote(value) {
  return /\s/.test(value) ? `"${value}"` : value;
}

// Rows outnumber the ones anyone opens, so the field list is built on demand.
const rowFields = new WeakMap();

// Reader.fs derives the row's own columns from these, so the panel omits them.
const shownFields = new Set([
  "MESSAGE",
  "PRIORITY",
  "SYSLOG_FACILITY",
  "SYSLOG_IDENTIFIER",
  "_HOSTNAME",
  "_PID",
  "_SYSTEMD_UNIT",
]);

function toggleDetails(row) {
  const details = row.querySelector(".details");
  if (!details.children.length) {
    for (const [name, value] of rowFields.get(row)) {
      if (shownFields.has(name)) continue;
      const term = document.createElement("dt");
      term.textContent = name;
      const description = document.createElement("dd");
      description.textContent = value;
      details.append(term, description);
    }
  }
  details.hidden = !details.hidden;
  const toggle = row.querySelector(".toggle");
  toggle.setAttribute("aria-expanded", String(!details.hidden));
  toggle.textContent = details.hidden ? "▸" : "▾";
}

function field(row, selector, text, filter, value) {
  const button = row.querySelector(selector);
  button.textContent = text ?? "—";
  button.disabled = value === null || value === undefined;
  if (!button.disabled) {
    button.onclick = () => appendFilter(filter, quote(value));
  }
  return button;
}

function render(
  {
    cursor,
    realtime,
    hostname,
    source,
    application,
    processId,
    unit,
    facility,
    severity,
    message,
    fields,
  },
  prepend = false,
) {
  if (document.querySelector(`[data-cursor="${cursor}"]`)) return;
  const row = template.content.firstElementChild.cloneNode(true);
  row.dataset.cursor = cursor;
  row.querySelector("time").textContent = formatTime(new Date(realtime));

  field(row, ".host", hostname ?? source, "host", hostname);
  field(
    row,
    ".app",
    application ? `${application}${processId ? `[${processId}]` : ""}` : null,
    "app",
    application,
  );
  field(row, ".unit", unit, "unit", unit);
  const facilityName = Number.isInteger(facility)
    ? facilityNames[facility]
    : null;
  field(row, ".facility", facilityName, "facility", facilityName);
  const severityName = Number.isInteger(severity)
    ? severityNames[severity]
    : null;
  field(row, ".severity", severityName, "severity", severityName);

  row.querySelector(".message").textContent = message;
  rowFields.set(row, fields);
  prepend ? list.prepend(row) : list.append(row);
}

async function load(append = false) {
  try {
    const params = parameters();
    if (append && cursor) params.set("before", cursor);
    const response = await fetch(`/api/logs?${params}`, {
      signal: controller.signal,
    });
    if (!response.ok) {
      if (response.status === 400) {
        throw new Error(await response.text());
      }
      throw new Error(`Search failed: HTTP ${response.status}`);
    }
    const page = await response.json();
    if (!append) list.replaceChildren();
    for (const item of page.items) render(item);
    cursor = page.nextBefore;
    if (!append) {
      liveAfter = page.liveAfter;
      liveSince = page.liveSince;
      const relative = /\b(?:since|until):now(?:[+-]\d+(?:\.\d+)?[smhdw])?\b/.test(search.value);
      effectiveRange.hidden = !relative;
      if (relative) {
        const start = page.effectiveSince
          ? new Date(page.effectiveSince).toLocaleString() : "beginning";
        effectiveRange.textContent = page.effectiveUntil
          ? `Effective time: ${start} to ${new Date(page.effectiveUntil).toLocaleString()} (end excluded)`
          : `Effective time: ${start} onward`;
      }
    }
    older.hidden = !cursor || page.items.length === 0;
    empty.hidden = list.children.length > 0;
    clearSearchError();
    return page;
  } catch (error) {
    if (error.name === "AbortError") return null;
    throw error;
  }
}

function connect() {
  stream?.close();
  const params = parameters();
  if (liveAfter) params.set("after", liveAfter);
  else if (liveSince) params.set("from", liveSince);
  stream = new EventSource(`/api/tail?${params}`);
  stream.onmessage = (event) => {
    liveAfter = event.lastEventId;
    render(JSON.parse(event.data), true);
    empty.hidden = true;
  };
  stream.onerror = () => {
    notice.textContent = "Live connection interrupted; reconnecting.";
  };
  liveButton.textContent = "⏸";
  liveButton.setAttribute("aria-label", "Pause live updates");
  liveButton.disabled = false;
}

async function refresh() {
  effectiveRange.hidden = true;
  controller?.abort();
  controller = new AbortController();
  stream?.close();
  stream = null;
  liveButton.disabled = true;
  const params = parameters();
  history.replaceState(null, "", params.size ? `?${params}` : "/");
  try {
    const page = await load();
    if (page) connect();
  } catch (error) {
    if (error.message.startsWith("invalid ")) showSearchError(error.message);
    else notice.textContent = error.message;
  }
}

form.onsubmit = (event) => {
  event.preventDefault();
  closeSuggestions();
  refresh();
};
search.oninput = () => {
  clearSearchError();
  showSuggestions();
};
search.onfocus = showSuggestions;
search.onkeydown = (event) => {
  if (event.key === "Escape") closeSuggestions();
  if (suggestions.hidden) return;
  if (event.key === "ArrowDown" || event.key === "ArrowUp") {
    event.preventDefault();
    selectedSuggestion = (selectedSuggestion +
      (event.key === "ArrowDown" ? 1 : -1) + suggestionItems.length) % suggestionItems.length;
    updateSelectedSuggestion();
  } else if (event.key === "Enter") {
    event.preventDefault();
    chooseSuggestion(selectedSuggestion);
  }
};
search.onblur = () => setTimeout(closeSuggestions, 100);
syntaxToggle.onclick = () => {
  syntaxHelp.hidden = !syntaxHelp.hidden;
  syntaxToggle.setAttribute("aria-expanded", String(!syntaxHelp.hidden));
  closeSuggestions();
};
document.addEventListener("click", (event) => {
  if (!event.target.closest(".search-area")) {
    syntaxHelp.hidden = true;
    syntaxToggle.setAttribute("aria-expanded", "false");
  }
});
list.onclick = (event) => {
  if (event.target.closest(".field") || getSelection().toString()) return;
  const row = event.target.closest("li");
  if (row) toggleDetails(row);
};
older.onclick = () =>
  load(true).catch((error) => {
    notice.textContent = error.message;
  });
liveButton.onclick = () => {
  if (stream) {
    stream.close();
    stream = null;
    liveButton.textContent = "▶";
    liveButton.setAttribute("aria-label", "Resume live updates");
  } else connect();
};

const initial = new URLSearchParams(location.search);
search.value = initial.get("q") || "";
refresh();
