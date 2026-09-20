# Zitat

Zitat is a web UI and HTTP API for systemd journals.

## Yes, really.

Zitat is written in F# on .NET, for a Linux-only, systemd-only tool: .NET is
fast on Linux, and Zitat's binary is self-contained, so no runtime install is
required. F#'s type system and pattern matching suit journal parsing and query
handling extremely well.

## Run locally

The .NET 10 SDK is required.

```sh
make test
make run
```

`make build`, `make publish`, and `make clean` are also available. The server
prints its listening URL on startup.

To read a journal directory other than `/var/log/journal`:

```sh
Zitat__JournalDirectory=/path/to/journal make run
```

## Configuration

Settings can be specified in `appsettings.json`, environment variables, or
command-line arguments. In environment variable names, `:` is replaced with
`__`.

| Setting                  | Default            | Description                      |
| ------------------------ | ------------------ | -------------------------------- |
| `Zitat:JournalDirectory` | `/var/log/journal` | Root of the journal tree to read |
