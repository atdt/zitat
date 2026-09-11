# Zitat: Lightweight Syslog Aggregator

## Goal

Build a small, self-hosted log aggregation service for personal/homelab use, roughly inspired by Papertrail.

The system should favor simplicity, low operational overhead, and easy debugging over scalability or enterprise features.

## Design Philosophy

Favor code that is easy to inspect, debug, replace, and operate from ordinary command-line tools. When in doubt, choose the simpler design.

## Environment

* Approximately 5–10 hosts.
* Hosts are spread across several sites, including home networks and a cloud VPS.
* Most hosts are Linux systems using common syslog/journald tooling.
* Some lightweight/network devices may only support traditional syslog.
* Hosts generally have Tailscale connectivity.
* Expected log volume is low, normally well under 10 MB/day.

## Client Log Shipping

Most Linux clients use systemd-journald as their primary local logging system.

The collector should be easy to use with common existing log-shipping mechanisms rather than requiring a custom client agent. In particular, clients may use journald together with rsyslog or similar tooling to forward logs as standard syslog.

systemd also provides native journal transport mechanisms such as `systemd-journal-upload` and `systemd-journal-remote`. Native journal ingestion is not required for the initial version, but the design should avoid unnecessarily precluding it as a future ingestion path.

## Ingestion

* Accept standard syslog from remote hosts.
* Support both UDP and TCP.
* Tolerantly handle common syslog formats, including RFC 3164 and RFC 5424.
* Occasional message loss is acceptable.
* Public Internet ingestion is not required initially.
* The expected Linux client path includes journald → existing syslog forwarding tooling → collector. Installing a collector-specific agent should not be necessary.

## Stored Data

Capture useful standard log metadata where available, including:

* receive timestamp
* sender/message timestamp
* hostname
* application/program name
* process ID
* facility
* severity
* message
* source address
* original/raw message

Retention should be based on the collector's receive time.

Multiline reconstruction is not required.

## Storage and Retention

* Use a simple local datastore suitable for a single-node deployment.
* SQLite is an expected fit but is not a hard requirement.
* Support configurable time-based retention, with roughly 7–30 days being typical.
* Support a configurable storage-size limit to protect against runaway logging.
* No replication, HA, or elaborate backup strategy is required.

## Search and Filtering

Users should be able to:

* search message text
* filter by hostname
* filter by application/program
* filter by facility
* filter by severity
* restrict results by time range
* combine filters

A small textual query syntax is desirable, alongside discoverable GUI controls.

The query model should remain simple and extensible rather than attempting to reproduce a full log-query language.

## Web UI

Provide a lightweight web interface similar in spirit to Papertrail:

* chronological log stream
* live tail
* pause/resume
* full-text search
* filter controls
* time-range selection
* easy navigation through historical logs
* clickable fields that can become filters
* URLs that can represent/share the current search where practical

The UI will normally only be reachable over Tailscale and does not require its own authentication.

## API

Expose the underlying search functionality through a simple HTTP API so logs can also be queried with tools such as `curl` or a future CLI.

Historical queries and live-tail queries should use compatible filtering semantics.

## Reliability and Safety

* The service should be a single modest daemon or similarly simple deployment.
* It should tolerate malformed input.
* It should protect itself from extreme per-source log floods, dropping excess messages if necessary while making the loss visible.
* Normal operation should require very little administration.

## Out of Scope for Initial Version

* dashboards and analytics
* distributed storage
* high availability
* complex authentication/authorization
* guaranteed delivery
* multiline event reconstruction
* saved searches
* alerting/notifications
* arbitrary structured-log parsing
* native systemd journal ingestion

Alerting may be added later, so the query model should not unnecessarily prevent a stored query from eventually being used as an alert condition.

## Implementation

The service should be implemented in F# using Falco for the HTTP layer and native SQLite for local storage.

Development will be done on macOS. The deployment target is the Linux host `imp`, accessible via:

```sh
ssh imp
```

`imp` is under strict configuration management. It is fine to inspect the system and determine what is already installed or how it is configured, but do not install packages, modify system configuration, or make persistent changes on `imp` without explicit permission.

## Writing

Write prose that reads like it was engineered to be unambiguous under legal
and congressional scrutiny. Unambiguity comes from naming things exactly, not
from hedging or qualifying them.

Cut any clause that only adds emphasis.

Comment sparsely, and only to prevent astonishment, resolve perplexity, or
keep Chesterton's fence standing.

## Git

Commit your work as you work through the implementation.

Write every Git commit message with a subject and a brief body. Wrap every
commit-message line at 72 characters.

Keep code clean and readable.

Keep working until you complete the implementation. You can log controversial
design decisions that you'd like me to weigh on later. Do the work first.

## Implementation Guidelines - *CRITICAL*

Prefer concise F# with short functions, short lines, small modules, and
judicious whitespace. Avoid long expressions, deeply nested pipelines, giant
handlers, and walls of code. Break complex operations into well-named local
functions and values. Favor readability and visual structure over minimizing
the number of declarations. Aim for roughly 80–100 columns where practical.

Prefer:
- short functions
- small modules
- narrow line lengths
- pattern matching over nested conditionals
- pipelines broken across lines
- local names instead of giant expressions
- whitespace between conceptual steps
