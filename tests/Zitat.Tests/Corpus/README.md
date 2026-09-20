# Synthetic journal corpus

`journal/` is a small journal tree for `CorpusTests.fs` to run against. All
of it is synthetic: hostnames are metal elements (`iron`, `cobalt`,
`nickel`), IPs are from the RFC 5737 documentation range
(`203.0.113.0/24`), and every log message was generated, not copied from a
real host.

It was produced with `systemd` actually running, so the binary format is
real. On macOS, that means running `systemd` as PID 1 inside a Linux
container, using Apple's `container` CLI
(<https://github.com/apple/container/blob/main/docs/how-to.md>):

1. Build a Debian image with `systemd` and `systemd-journal-remote`
   installed, `CMD ["/lib/systemd/systemd"]`.
2. Boot one container per fake host, set its hostname, and log synthetic
   entries under it — varying syslog priority, some multi-line and Unicode
   messages, some run under `systemd-run` transient units (to populate
   `_SYSTEMD_UNIT`), spread across several days by moving the container's
   clock backward before each batch and restoring it at the end.
3. `journalctl --rotate` to get an active file plus rotated archives, so
   the corpus exercises the reader's cross-file chaining.
4. Copy the resulting `*.journal` files out. For the file(s) standing in
   for a remote sender, rename to systemd-journal-remote's
   `remote-<address>@<boot-id>-<seqnum>-<realtime>.journal` convention.

Regenerate by repeating the above with a fresh fake hostname or IP if the
corpus needs to grow or change shape.
