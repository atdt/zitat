# Zitat

Zitat is a web UI and HTTP API for systemd journals.

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
command-line arguments. In environment variable names, `:` is replaced with `__`.

| Setting | Default | Description |
|---|---|---|
| `Zitat:JournalDirectory` | `/var/log/journal` | Root of the journal tree to read |
