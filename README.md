# Zitat

Zitat is a single-node syslog collector for small private networks. It accepts
RFC 3164 and RFC 5424 messages over UDP and TCP, stores them in SQLite, and
provides a searchable web log stream and HTTP API.

## Run locally

.NET 10 SDK is required for development.

```sh
make test
make run
```

`make build`, `make publish`, and `make clean` provide the other common
repository workflows. The Makefile delegates compilation and packaging to the
.NET project files.

The web interface uses the address printed by ASP.NET Core. The syslog
listeners use UDP and TCP port 5514 by default.

Send test messages with `logger` or `nc`:

```sh
logger --server 127.0.0.1 --port 5514 --tcp "TCP test"
printf '<13>Sep 11 10:30:00 router test[42]: UDP test\n' \
  | nc -u -w 1 127.0.0.1 5514
```

## Configuration

Settings come from `appsettings.json`, environment variables, or ASP.NET Core
command-line configuration. Environment variable names replace `:` with `__`.

| Setting | Default | Meaning |
|---|---:|---|
| `Zitat:DatabasePath` | `zitat.db` | SQLite database path |
| `Zitat:UdpPort` | `5514` | UDP listen port |
| `Zitat:TcpPort` | `5514` | TCP listen port |
| `Zitat:RetentionDays` | `14` | Maximum receive-time age |
| `Zitat:MaxStorageBytes` | `1073741824` | Database, WAL, and SHM byte limit |
| `Zitat:MaxMessageBytes` | `65536` | Maximum accepted frame size |
| `Zitat:FloodMessagesPerSecond` | `500` | Refill rate for each source |
| `Zitat:FloodBurst` | `1000` | Initial and maximum source allowance |

The collector checks retention and storage size once per minute. It deletes
expired records first. If the SQLite files still exceed the size limit, it
deletes the oldest records in batches and reclaims their pages.

## Linux forwarding

Rsyslog can forward both native syslog input and messages read from journald.
The following action uses TCP and the RFC 5424 forwarding template:

```text
*.* action(
  type="omfwd"
  target="COLLECTOR_TAILSCALE_ADDRESS"
  port="5514"
  protocol="tcp"
  template="RSYSLOG_SyslogProtocol23Format"
  action.resumeRetryCount="-1"
  queue.type="linkedList"
  queue.filename="zitat"
)
```

On systems where rsyslog is configured to read the journal, no Zitat-specific
client agent is required. The exact rsyslog input configuration remains the
responsibility of each host's configuration management.

Traditional devices can send UDP syslog to the collector's port 5514. Use port
514 instead only if deployment configuration grants the service permission to
bind a privileged port.

## Search API

`GET /api/logs` returns newest-first results. It accepts these parameters:

| Parameter | Meaning |
|---|---|
| `q` | Message text and textual filters |
| `host` | Exact, case-insensitive hostname |
| `app` | Exact, case-insensitive application |
| `source` | Exact, case-insensitive sender address |
| `facility` | Numeric syslog facility, 0 through 23 |
| `severity` | Syslog severity 0 through 7, optionally compared |
| `since` | Inclusive ISO 8601 receive timestamp |
| `until` | Inclusive ISO 8601 receive timestamp |
| `range` | Relative range such as `15m`, `6h`, or `7d` |
| `before` | Return records with a lower ID for pagination |
| `limit` | Page size from 1 through 1000; default 200 |

The textual syntax recognizes `host:`, `app:`, `source:`, `facility:`, and
`severity:`. Remaining terms form one case-insensitive message substring.
Double quotes keep spaces together.

Severity accepts `<=`, `<`, `>=`, and `>` before the number. Severity counts
down from 0, so `severity:<=3` reads "error and worse" and is the usual way to
ask for the messages that matter. A bare number still matches that severity
alone. Messages that carry no severity match neither form.

`source:` filters on the address the message arrived from. It is the only way
to isolate a device whose output is malformed enough to carry no hostname.

```sh
curl --get http://127.0.0.1:8080/api/logs \
  --data-urlencode 'q=host:router app:dhcpd "lease granted"' \
  --data-urlencode 'range=24h'
```

```sh
curl --get http://127.0.0.1:8080/api/logs \
  --data-urlencode 'q=severity:<=3' \
  --data-urlencode 'range=1h'
```

`GET /api/tail` is a server-sent event stream and accepts the same filters.

```sh
curl -N 'http://127.0.0.1:8080/api/tail?severity=3&range=1h'
```

`GET /api/status` reports stored-message and byte counts. It also reports
malformed input, rate-limit drops, full-queue drops, and storage failures.
`GET /health` is the process health check.

## Deployment

Build the self-contained Linux ARM64 archive for `imp`:

```sh
scripts/publish-linux-arm64.sh
```

The output is `artifacts/zitat-linux-arm64.tar.gz`. The repository includes a
sample [systemd service](deploy/zitat.service) and
[environment file](deploy/zitat.env.example). They are deployment inputs, not
an installer. Applying them to `imp` requires an explicit configuration
management change.

The service uses a dynamic system user and systemd's `/var/lib/zitat` state
directory. Set `ASPNETCORE_URLS` to a Tailscale address when the UI must be
reachable from other tailnet hosts. Binding to `0.0.0.0` exposes the HTTP
listener on every interface and must be paired with host firewall rules.

## Operational notes

- Malformed messages are stored with their original bytes decoded as UTF-8.
- TCP accepts newline-delimited and RFC 6587 octet-counted frames.
- Live clients have independent bounded queues. A slow browser cannot block
  ingestion.
- SQLite uses WAL mode. Stop the service before copying the database as a
  single-file backup.
- All retention decisions use the collector's receive timestamp.

See [design decisions](docs/design-decisions.md) for implementation boundaries.
