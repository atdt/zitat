namespace Zitat.Journal

open System

type Direction =
    | Newest
    | Oldest

/// Each term accepts any of its DATA objects. An entry matches when it
/// references an accepted object for every term. A severity range forms one
/// term with an object for each accepted priority.
type Term = { Field: string; Objects: int64[] }

[<Sealed>]
type private ChainCursor(chain: EntryArrayChain, direction: Direction) =
    let count = chain.Count
    let mutable index = -1L

    member _.Current =
        if index < 0L || index >= count then
            ValueNone
        else
            ValueSome chain[index]

    /// Positions at or below `bound` for Newest, or at or above it for Oldest.
    /// Without a bound, starts at the corresponding end of the chain.
    member this.Seek(bound: int64 voption) =
        index <-
            match direction, bound with
            | Newest, ValueNone -> count - 1L
            | Oldest, ValueNone -> 0L
            | Newest, ValueSome offset ->
                let position = chain.LowerBoundOffset offset

                if position < count && chain[position] = offset then
                    position
                else
                    position - 1L
            | Oldest, ValueSome offset -> chain.LowerBoundOffset offset

        this.Current

    member _.MoveNext() =
        index <-
            (match direction with
             | Newest -> index - 1L
             | Oldest -> index + 1L)

        if index < 0L || index >= count then
            ValueNone
        else
            ValueSome chain[index]

/// Merges the chains for one term into an ordered stream.
[<Sealed>]
type private UnionCursor(chains: EntryArrayChain[], direction: Direction) =
    let cursors = chains |> Array.map (fun chain -> ChainCursor(chain, direction))
    let mutable current = ValueNone

    let recompute () =
        let mutable best = ValueNone

        for cursor in cursors do
            match cursor.Current, best with
            | ValueNone, _ -> ()
            | ValueSome offset, ValueNone -> best <- ValueSome offset
            | ValueSome offset, ValueSome chosen ->
                let better =
                    match direction with
                    | Newest -> offset > chosen
                    | Oldest -> offset < chosen

                if better then
                    best <- ValueSome offset

        current <- best
        best

    member _.Current = current

    member _.Seek(bound: int64 voption) =
        for cursor in cursors do
            cursor.Seek bound |> ignore

        recompute ()

    member _.MoveNext() =
        // Advance every chain at this offset.
        match current with
        | ValueNone -> ValueNone
        | ValueSome emitted ->
            for cursor in cursors do
                if cursor.Current = ValueSome emitted then
                    cursor.MoveNext() |> ignore

            recompute ()

/// Scans candidates from the term with the fewest entries.
[<Sealed>]
type FileScan(file: JournalFile, terms: Term list, direction: Direction, bound: int64 voption) =
    let chainsFor (term: Term) =
        term.Objects |> Array.map file.DataChain

    let sized =
        terms
        |> List.map (fun term ->
            let chains = chainsFor term
            let size = chains |> Array.sumBy _.Count
            size, term, chains)

    let unsatisfiable = sized |> List.exists (fun (size, _, _) -> size = 0L)

    let driver, others =
        match sized with
        | [] -> UnionCursor([| file.GlobalChain() |], direction), []
        | _ ->
            let chosen =
                sized |> List.indexed |> List.minBy (fun (_, (size, _, _)) -> size) |> fst

            let _, _, chains = sized[chosen]

            let rest =
                sized
                |> List.indexed
                |> List.filter (fun (index, _) -> index <> chosen)
                |> List.map (fun (_, (_, term, _)) -> term)

            UnionCursor(chains, direction), rest

    let satisfies entryOffset =
        others
        |> List.forall (fun term ->
            term.Objects
            |> Array.exists (fun object' -> file.EntryReferences(entryOffset, object')))

    let mutable started = false

    member _.File = file

    member _.Next() =
        if unsatisfiable then
            ValueNone
        else
            let mutable candidate =
                if started then
                    driver.MoveNext()
                else
                    started <- true
                    driver.Seek bound

            let mutable result = ValueNone

            while result.IsNone && candidate.IsSome do
                let offset = candidate.Value

                if satisfies offset then
                    result <- ValueSome offset
                else
                    candidate <- driver.MoveNext()

            result
