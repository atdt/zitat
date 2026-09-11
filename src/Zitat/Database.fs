namespace Zitat

open System
open System.Globalization
open System.IO
open Microsoft.Data.Sqlite

type Database(path: string) =
    let connectionString =
        SqliteConnectionStringBuilder(
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        ).ToString()

    let openConnection () =
        let connection = new SqliteConnection(connectionString)
        connection.Open()
        connection

    let unixMilliseconds (value: DateTimeOffset) = value.ToUnixTimeMilliseconds()
    let fromUnixMilliseconds value = DateTimeOffset.FromUnixTimeMilliseconds value

    let addOptional (command: SqliteCommand) name dbType value =
        let parameter = command.Parameters.Add(name, dbType)
        parameter.Value <- value |> Option.map box |> Option.defaultValue DBNull.Value

    let optionalString (reader: SqliteDataReader) ordinal =
        if reader.IsDBNull ordinal then None else Some(reader.GetString ordinal)

    let optionalInt (reader: SqliteDataReader) ordinal =
        if reader.IsDBNull ordinal then None else Some(reader.GetInt32 ordinal)

    let optionalTimestamp (reader: SqliteDataReader) ordinal =
        if reader.IsDBNull ordinal then None
        else Some(fromUnixMilliseconds (reader.GetInt64 ordinal))

    let readEntry (reader: SqliteDataReader) =
        {
            Id = reader.GetInt64 0
            ReceivedAt = fromUnixMilliseconds (reader.GetInt64 1)
            SentAt = optionalTimestamp reader 2
            Hostname = optionalString reader 3
            Application = optionalString reader 4
            ProcessId = optionalString reader 5
            Facility = optionalInt reader 6
            Severity = optionalInt reader 7
            Message = reader.GetString 8
            SourceAddress = reader.GetString 9
            RawMessage = reader.GetString 10
        }

    let databaseFiles () =
        [ path; path + "-wal"; path + "-shm" ]
        |> List.choose (fun file ->
            if File.Exists file then Some(FileInfo(file).Length) else None)

    member _.Path = path

    member _.Initialize() =
        let directory =
            Path.GetDirectoryName(Path.GetFullPath path)
            |> Option.ofObj
            |> Option.defaultValue "."
        Directory.CreateDirectory directory |> ignore

        use connection = openConnection ()
        use command = connection.CreateCommand()
        command.CommandText <-
            """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
            PRAGMA auto_vacuum = INCREMENTAL;

            CREATE TABLE IF NOT EXISTS logs (
                id INTEGER PRIMARY KEY,
                received_at INTEGER NOT NULL,
                sent_at INTEGER,
                hostname TEXT,
                application TEXT,
                process_id TEXT,
                facility INTEGER,
                severity INTEGER,
                message TEXT NOT NULL,
                source_address TEXT NOT NULL,
                raw_message TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS logs_received_at
                ON logs(received_at, id);
            CREATE INDEX IF NOT EXISTS logs_hostname
                ON logs(hostname, received_at);
            CREATE INDEX IF NOT EXISTS logs_application
                ON logs(application, received_at);
            CREATE INDEX IF NOT EXISTS logs_facility
                ON logs(facility, received_at);
            CREATE INDEX IF NOT EXISTS logs_severity
                ON logs(severity, received_at);
            """
        command.ExecuteNonQuery() |> ignore

    member _.Insert(item: PendingLogEntry) =
        use connection = openConnection ()
        use command = connection.CreateCommand()
        command.CommandText <-
            """
            INSERT INTO logs (
                received_at, sent_at, hostname, application, process_id,
                facility, severity, message, source_address, raw_message
            ) VALUES (
                $received, $sent, $host, $app, $pid,
                $facility, $severity, $message, $source, $raw
            );
            SELECT last_insert_rowid();
            """

        command.Parameters.AddWithValue("$received", unixMilliseconds item.ReceivedAt)
        |> ignore
        addOptional command "$sent" SqliteType.Integer (item.SentAt |> Option.map unixMilliseconds)
        addOptional command "$host" SqliteType.Text item.Hostname
        addOptional command "$app" SqliteType.Text item.Application
        addOptional command "$pid" SqliteType.Text item.ProcessId
        addOptional command "$facility" SqliteType.Integer item.Facility
        addOptional command "$severity" SqliteType.Integer item.Severity
        command.Parameters.AddWithValue("$message", item.Message) |> ignore
        command.Parameters.AddWithValue("$source", item.SourceAddress) |> ignore
        command.Parameters.AddWithValue("$raw", item.RawMessage) |> ignore

        let id = command.ExecuteScalar() :?> int64
        { Id = id; ReceivedAt = item.ReceivedAt; SentAt = item.SentAt
          Hostname = item.Hostname; Application = item.Application
          ProcessId = item.ProcessId; Facility = item.Facility
          Severity = item.Severity; Message = item.Message
          SourceAddress = item.SourceAddress; RawMessage = item.RawMessage }

    member _.Search(query: LogQuery) =
        use connection = openConnection ()
        use command = connection.CreateCommand()
        let clauses = ResizeArray<string>()

        let add name dbType value clause =
            match value with
            | Some actual ->
                clauses.Add clause
                addOptional command name dbType (Some actual)
            | None -> ()

        add "$text" SqliteType.Text query.Text "instr(lower(message), lower($text)) > 0"
        add "$host" SqliteType.Text query.Hostname "hostname = $host COLLATE NOCASE"
        add "$app" SqliteType.Text query.Application "application = $app COLLATE NOCASE"
        add "$facility" SqliteType.Integer query.Facility "facility = $facility"
        add "$severity" SqliteType.Integer query.Severity "severity = $severity"
        add "$since" SqliteType.Integer (query.Since |> Option.map unixMilliseconds)
            "received_at >= $since"
        add "$until" SqliteType.Integer (query.Until |> Option.map unixMilliseconds)
            "received_at <= $until"
        add "$before" SqliteType.Integer query.BeforeId "id < $before"

        let where =
            if clauses.Count = 0 then ""
            else " WHERE " + String.Join(" AND ", clauses)

        command.CommandText <-
            "SELECT id, received_at, sent_at, hostname, application, "
            + "process_id, facility, severity, message, source_address, "
            + "raw_message FROM logs"
            + where
            + " ORDER BY id DESC LIMIT $limit"
        command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 1000))
        |> ignore

        use reader = command.ExecuteReader()
        let results = ResizeArray<LogEntry>()
        while reader.Read() do results.Add(readEntry reader)
        List.ofSeq results

    member _.DeleteBefore(cutoff: DateTimeOffset) =
        use connection = openConnection ()
        use command = connection.CreateCommand()
        command.CommandText <- "DELETE FROM logs WHERE received_at < $cutoff"
        command.Parameters.AddWithValue("$cutoff", unixMilliseconds cutoff) |> ignore
        command.ExecuteNonQuery()

    member _.DeleteOldest(limit: int) =
        use connection = openConnection ()
        use command = connection.CreateCommand()
        command.CommandText <-
            "DELETE FROM logs WHERE id IN "
            + "(SELECT id FROM logs ORDER BY id LIMIT $limit)"
        command.Parameters.AddWithValue("$limit", limit) |> ignore
        command.ExecuteNonQuery()

    member _.Compact() =
        use connection = openConnection ()
        use command = connection.CreateCommand()
        command.CommandText <- "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA incremental_vacuum;"
        command.ExecuteNonQuery() |> ignore

    member _.SizeBytes = databaseFiles () |> List.sum

    member _.Count =
        use connection = openConnection ()
        use command = connection.CreateCommand()
        command.CommandText <- "SELECT count(*) FROM logs"
        Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture)
