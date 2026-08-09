# The Pegasus note format

One note is one `.pegasus` file. The file is the authoritative replica of that
note on this machine; the `.md` beside it is a projection and is never read back.

The design argument that selects this format is in `Pegasus_Design.md` §2 and
§4.1; what follows is the format itself and what it guarantees.

## 1. Layout

    header   32 bytes
      0..3    magic  "PGSS"
      4       schema version (currently 1)
      5..7    reserved, zero
      8..23   note id, a GUID
      24..31  created-at, Unix milliseconds, little endian

    record   repeated to end of file
      0..3    payload length, uint32 little endian
      4..7    CRC-32 (IEEE) of the payload
      8..     payload: one Yjs update, exactly as ObserveUpdatesV1 delivered it

The schema version is in the header from the first release. This is deliberate
and cheap: the sibling EmuSen project has a documented case where a schema was
edited and silently did nothing to existing files that held real history, because
nothing in the file recorded which schema it was written under. A file that
cannot say what it is cannot be migrated safely.

## 2. Why append-only

The alternative is to serialise the whole document on every change. That is
simpler to write and strictly worse to crash inside: a process killed midway
through rewriting a file leaves neither the old contents nor the new.

Appending has the opposite failure mode. A crash mid-write leaves a trailing
record that is short, or whose CRC does not match. On the next open that record
is detected and dropped, and the file truncates to the last byte of the last
intact record. Nothing earlier is at risk, because nothing earlier was being
written. The dropped update is one edit — typically a few characters — and the
document is otherwise whole.

This is the property that lets the durability claim be stated plainly: a crash
costs at most the operation in flight.

## 3. Compaction

The log grows without bound, and replaying thousands of small updates at open is
wasteful. Compaction collapses it: the whole document is encoded as a single Yjs
update and written, with a fresh header, to `<name>.pegasus.compacting`. That
file is flushed to physical media, and only then is it renamed over the original.

`rename(2)` is atomic on Linux. A reader therefore sees either the complete old
file or the complete new one, never a blend, and a crash at any point during
compaction leaves a valid file. The temp file is the only thing that can be
orphaned, and an orphan is harmless.

Compaction is not a checkpoint that can be lost. It is a rewrite of information
already durably present.

Note that the atomicity argument is a Linux one. `File.Move(..., overwrite: true)`
is the same call on the other two RIDs this project publishes for, but the
guarantee it carries there has not been verified, and this section should not be
read as claiming it has.

## 4. What `Append` guarantees, and what it does not

`Append` writes the record and calls `Flush()`, which pushes the bytes to the
operating system. It does **not** call `fsync`.

The distinction matters and is worth being exact about:

- **Process crash** — the application is killed, `kill -9`, an unhandled
  exception. Nothing is lost. The bytes are already in the kernel's page cache
  and the OS writes them out regardless of what happened to the process.
- **Power loss or kernel panic** — the tail of the log can be lost, bounded by
  how long the page cache held it.

`Sync()` forces the tail to media and is called on close and when the session
goes idle, not per keystroke. Calling `fsync` on every character typed would make
the editor feel wrong to use, and would buy protection only against the narrower
of the two failures.

## 5. The Markdown projection

Beside `ideas.pegasus` sits `ideas.md`, containing the note's current text and
nothing else. It exists so the notes are readable by any editor, greppable, and
committable to a repository if someone wants that.

It is written through a temp file and a rename, so it is never observed
half-written.

It is **never read back**. This is the whole discipline: the moment a projection
becomes an input, editing it out of band silently diverges from the replica, and
the question "which of these two is right?" has no good answer. Pegasus answers
it by construction — the `.pegasus` file is right, always, and the `.md` is
regenerated from it.

If someone edits the `.md`, their changes are overwritten on the next keystroke.
That is a real sharp edge and it is the price of the guarantee.

## 6. What this format does not do

No encryption at rest. The file sits in the user's own filesystem under whatever
protection that filesystem provides. Encrypting it would need a key, and a key
needs somewhere to live; that is a larger design than this project has taken on.

No per-author attribution in the file, though Yjs retains it internally and the
log would support extracting it later.

No cross-note transactions. Each note is independent, which is why the workspace
index (`Pegasus_Sync.md` §6) is itself just another note rather than a schema.
