# Design decisions

## The journal is the datastore

Zitat keeps no store of its own. An earlier version accepted syslog over
UDP/TCP and wrote SQLite; `systemd-journal-remote` does that transport better,
and keeping a second copy of the same log data bought nothing the journal does
not already provide.

Removing it also removed the reason the syslog listener existed. The journal
carries `_SYSTEMD_UNIT`, `_BOOT_ID`, `_UID`, `_COMM` and `_CMDLINE`; the syslog
path reached us only after rsyslog had flattened those away.

## Reading the format rather than shelling out

`journalctl --output=json` would have answered every query this interface
makes. Reading the binary format directly costs roughly a thousand lines but
removes a process spawn and a JSON parse from each request, and gives the query
planner access to the journal's own indexes rather than only to journalctl's
command line.

## Refusing files instead of guessing at them

A file whose `HEADER_INCOMPATIBLE_*` flags are not all implemented is refused,
and there is no fallback to `journalctl`. A reader that proceeds past a feature
it does not understand returns wrong entries, and wrong logs are worse than an
error that says which flag is missing.

Structural corruption is handled the opposite way: the file is skipped and the
rest of the tree is still served. The format documentation asks readers to
degrade around damage, and a file being created right now looks the same as a
damaged one.

## One driver term, the rest tested per candidate

A query becomes a conjunction of disjunctions over DATA objects: `severity:<=3`
is one term holding four priority values. Rather than intersecting every term's
entry array chain in parallel, the term with the fewest entries drives the
iteration and the others are tested against each candidate.

The driver is the most selective filter available, so the number of candidates
examined is already bounded by the best index the query offers. Testing a
candidate compares integers against the entry's item array, which the format
documentation notes is short. This stays within a constant factor of a full
k-way intersection and avoids its machinery.

Equality filters resolve to a DATA object offset once per file, so testing an
entry never decodes a payload. Only the unindexed message substring does, and
it compares a field-name prefix straight off the mapping to avoid decoding
fields it does not want.

## Exact, case-sensitive field matching

Field filters are hash lookups over the stored bytes, so they match exactly.
The previous SQLite schema compared `COLLATE NOCASE`; preserving that would
have meant scanning instead of seeking. `journalctl _HOSTNAME=imp` behaves the
same way, and the in-memory filter used for the live stream matches these
semantics so that the stream cannot show an entry a search would miss.

## Cursors rather than row identifiers

Journal entries have no database identity, so pagination is a cursor holding
the writing file's `seqnum_id`, the entry's sequence number, and its timestamp.
Paging resumes below that position; tailing resumes above it. Entries with the
same timestamp and sequence number are ordered by `seqnum_id`.

## Live tail is durable

The follower refreshes the journal file set and signals live subscribers.
Subscribers read from the journal after their own cursors; notifications can
coalesce without dropping entries. A history response supplies a cursor for
the live stream, and SSE event IDs let reconnecting clients resume. Replay is
limited to entries still retained in the journal.

## Serialised remapping

Journal files that systemd is still writing grow, and picking up the new bytes
means remapping. Unmapping a file while another thread reads it is a
segmentation fault rather than an exception, so refreshing the file set and
reading from it hold the same lock. Both are short: a refresh stats the
directory, a query runs in milliseconds.
