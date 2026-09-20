# `imp` inspection

Inspection on 2026-09-11 found:

- Debian on ARM64 (`aarch64`).
- systemd 261.
- no `dotnet` executable in `PATH`.
- no existing `zitat.service` unit.
- no existing `/var/lib/zitat` state directory.

These findings support a self-contained `linux-arm64` release and a systemd
unit managed by the host's configuration management. No system configuration
was changed during inspection or artifact testing.

systemd 261 writes journal files with `KEYED_HASH`, `COMPACT` and
`COMPRESSED_ZSTD` set, which is the combination the reader implements.
