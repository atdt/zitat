# Zitat

Zitat is a search interface for systemd journals. It reads journal files
directly — the binary format, not `journalctl` output — and serves a searchable
web stream and HTTP API over every sender a host has collected.

It stores nothing of its own. The journal is the datastore; Zitat is the query
layer over it.

## Run locally

.NET 10 SDK is required for development.

```sh
make test
make run
```

`make build`, `make publish`, and `make clean` cover the other repository
workflows. The web interface uses the address printed by ASP.NET Core.

To read a journal tree other than the host's own:

```sh
Zitat__JournalDirectory=./testdata/journal make run
```

## Configuration

Settings come from `appsettings.json`, environment variables, or ASP.NET Core
command-line configuration. Environment variable names replace `:` with `__`.

| Setting | Default | Meaning |
|---|---|---|
| `Zitat:JournalDirectory` | `/var/log/journal` | Root of the journal tree to read, searched recursively |

The sample [systemd unit](deploy/zitat.service) sets `Type=notify` so systemd
waits for the readiness notification sent by `UseSystemd()` in `Program.fs`.

Retention and disk limits belong to systemd, not to Zitat. Configure them in
`journald.conf` and `journal-remote.conf`.

## Collecting logs

Zitat reads whatever is under its journal directory. Nothing needs to forward
to Zitat itself.

On each host that should ship logs, enable `systemd-journal-upload` pointed at
the collector:

```ini
# /etc/systemd/journal-upload.conf
[Upload]
URL=http://COLLECTOR_TAILSCALE_ADDRESS:19532
```

On the collector, `systemd-journal-remote` receives them and writes one file
per sender under `/var/log/journal/remote`. Set its retention there:

```ini
# /etc/systemd/journal-remote.conf
[Remote]
Seal=false
SplitMode=host
MaxUse=4G
```

Entries arrive with their `_SYSTEMD_UNIT`, `_BOOT_ID`, `_UID`, `_COMM` and
`_CMDLINE` intact, which a syslog transport would have flattened away. Those
fields are only as trustworthy as the sender that supplied them.

A device that speaks only syslog can still be covered without Zitat growing a
listener: have rsyslog on the collector accept it (`imudp`) and write it to the
local journal (`omjournal`), which lands under the same tree.

## Search API

`GET /api/logs` returns newest-first results.

| Parameter | Meaning |
|---|---|
| `q` | Message text and textual filters |
| `host` | `_HOSTNAME` |
| `app` | `SYSLOG_IDENTIFIER` |
| `unit` | `_SYSTEMD_UNIT` |
| `boot` | `_BOOT_ID` |
| `source` | Sending host, from the journal file name |
| `facility` | `SYSLOG_FACILITY`, 0 through 23 |
| `severity` | `PRIORITY` 0 through 7, optionally compared |
| `since` | Inclusive ISO 8601 timestamp |
| `until` | Inclusive ISO 8601 timestamp |
| `range` | Relative range such as `15m`, `6h`, or `7d` |
| `before` | Cursor from a previous page's `nextBefore` |
| `limit` | Page size from 1 through 1000; default 200 |

The textual syntax recognises `host:`, `app:`, `unit:`, `source:`, `boot:`,
`facility:`, and `severity:`. Remaining terms form one case-insensitive
substring of `MESSAGE`. Double quotes keep spaces together. Facility and
severity accept their standard names or numeric values, and severity accepts
`<=`, `<`, `>=`, and `>` before either.

Severity counts down from 0, so `severity:<=3` means error and worse.

**Field filters match exactly, and are case-sensitive.** They are served by the
journal's own hash index, which is a hash over the stored bytes; `host:imp` and
`host:IMP` are different lookups. This matches `journalctl _HOSTNAME=imp`.

`GET /api/tail` is the same query as a Server-Sent Events stream.
`GET /api/status` reports the files, entry count and senders currently visible.

## Journal format support

Zitat implements the format as documented in systemd's
[JOURNAL_FILE_FORMAT.md](https://systemd.io/JOURNAL_FILE_FORMAT), reading files
whose incompatible flags are within `KEYED_HASH | COMPACT | COMPRESSED_ZSTD` —
which is what systemd has written by default since 246.

A file carrying any other incompatible flag is **refused, loudly**. There is no
fallback path, because a reader that guesses at a feature it does not implement
shows wrong logs rather than no logs. If a systemd release turns on a new
incompatible feature, the reader needs teaching before it can read those files.

Corruption is treated differently: a file that fails its structural checks is
skipped with a warning and the rest of the tree is still served, as the format
documentation requires of readers.

## Tests

`make test` runs the unit tests. Tests that need real journal files look for a
tree at `testdata/journal` and skip when it is absent — a corpus is a copy of a
live host's logs, too large and too personal to keep in the repository. To run
them, copy one in:

```sh
rsync -a --rsync-path='sudo rsync' COLLECTOR:/var/log/journal/ testdata/journal/
```

Those tests re-derive the hash of every field of every sampled entry and assert
it resolves to the object the entry references, which exercises SipHash, the
hash tables, compact offsets and decompression together.
