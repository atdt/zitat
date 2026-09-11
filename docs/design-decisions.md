# Design decisions

## SQLite substring search

Zitat uses an indexed SQLite table for metadata and receive-time filters. Text
search uses a case-insensitive substring comparison instead of SQLite FTS5.
Substring behavior is predictable for punctuation-heavy log messages and does
not require a parallel search index. It scans the candidate time range, which
is acceptable for the specified volume and retention period.

An FTS table becomes justified if measured query latency is unacceptable. That
change can preserve the current `LogQuery` model and HTTP API.

## Numeric facility and severity

The datastore and API expose facility and severity as their syslog numeric
values. This avoids committing the query model to one display-name vocabulary.
The browser maps severity numbers to standard names for display.

## In-process live distribution

Stored messages are published to bounded, per-client channels after the SQLite
insert succeeds. Live tail therefore reports only records that historical
search can return. Live delivery is not durable; clients reconnect after a
network interruption and can repeat the historical query.

## Flood accounting

Each source address has an in-memory token bucket. Excess input and full ingest
queue writes are dropped. Cumulative counters in `/api/status` make these losses
observable. Counters reset when the process starts.

## Journal transport

The initial service accepts syslog only. The storage writer consumes a
`PendingLogEntry` channel rather than receiving directly from a socket. A future
journal receiver can submit normalized records through the same boundary.
