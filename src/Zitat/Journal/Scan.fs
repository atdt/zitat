namespace Zitat.Journal

open System

/// Which end of the stream iteration starts from.
type Direction =
    | Newest
    | Oldest

/// A conjunction of disjunctions over DATA objects within one file: an entry
/// matches when, for every term, it references at least one of that term's
/// objects. `host:iron severity:<=3` becomes two terms, the second holding
/// one object per admitted priority.
///
/// A term that resolved to no objects at all cannot match anything, which the
/// planner detects before iterating.
type Term = { Field: string; Objects: int64[] }

/// A position in one entry array chain, movable in a fixed direction.
[<Sealed>]
type private ChainCursor(chain: EntryArrayChain, direction: Direction) =
    let count = chain.Count
    let mutable index = -1L

    member _.Current =
        if index < 0L || index >= count then
            ValueNone
        else
            ValueSome chain[index]

    /// Positions at the first entry at or beyond `bound`, or at the extreme
    /// end of the chain when unbounded.
    member this.Seek(bound: int64 voption) =
        index <-
            match direction, bound with
            | Newest, ValueNone -> count - 1L
            | Oldest, ValueNone -> 0L
            | Newest, ValueSome offset ->
                // The last entry at or below the bound.
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

/// The union of several chains, presented as one ordered stream. A term such
/// as `severity:<=3` spans one chain per admitted priority value.
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
        // Every chain sitting on the emitted offset advances; a single entry
        // may be referenced by several of a term's objects.
        match current with
        | ValueNone -> ValueNone
        | ValueSome emitted ->
            for cursor in cursors do
                if cursor.Current = ValueSome emitted then
                    cursor.MoveNext() |> ignore

            recompute ()

/// Iterates the entries of one file that satisfy every term, in `direction`,
/// starting from an optional entry-offset bound.
///
/// One term drives the iteration and the rest are tested per candidate. The
/// driver is the term with the fewest entries, so the number of candidates
/// examined is bounded by the most selective filter available. Testing a
/// candidate against the remaining terms compares integers against the entry's
/// item array, which the format documentation notes is short (under 30 items),
/// so this stays within a constant factor of a full k-way intersection while
/// avoiding its machinery.
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

    // A term matching nothing makes the whole conjunction unsatisfiable.
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

    /// Advances to the next matching entry offset.
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
