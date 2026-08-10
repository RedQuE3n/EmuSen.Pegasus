# Pegasus — working agreement

A live shared notepad in F#/Avalonia, CRDT-backed, where no party may lose
information. **This is an academic project.** The reasoning is the deliverable
as much as the code, and it must survive somebody reading it critically.

## The man pages

`docs/` is the man pages. Three of them, section-numbered:

| Man page | Covers |
|---|---|
| `docs/Pegasus_Design.md` | Why a CRDT, why F#, Phase 0 evidence, dependency defects, assembly boundaries, testing discipline |
| `docs/Pegasus_Format.md` | The `.pegasus` file, append-only log, compaction, durability, the `.md` projection |
| `docs/Pegasus_Sync.md` | Topology, pairing, frame layout, the exchange, what the encryption is and is not |

`README.md` is the front door for a reader who has not opened the code. It
summarises; it never becomes the place a decision is recorded.

## Code notes go in the man pages, not the code body

**A note in the code body is one line and cites where the real note lives.**
The reasoning, the evidence, the alternatives considered and the measurements
all go into the appropriate man page under a numbered section. The code carries
a pointer to it.

    /// Threat model in Pegasus_Sync.md §5.
    /// Rewrites the log as one snapshot past this many records. See Pegasus_Format.md §3.

Not this — the body belongs in a `§`:

    /// Fixed salt: both peers must derive the same key from the code alone,
    /// with no round trip to agree on a random one. This means a code reused
    /// across sessions yields the same key, which is why ...

Rules that follow from it:

- One line. If it does not fit on one line, the note is a man-page section and
  the code gets a citation to it.
- Cite by file and section: `Pegasus_Sync.md §3`, not "see the docs".
- Every `§` cited from code must resolve. Adding a citation to a section that
  does not exist is a broken reference; write the section.
- Signatures, types and names carry meaning that comments otherwise would.
  Prefer renaming over explaining.
- Existing multi-line blocks stay until that file is being edited anyway; then
  collapse them and move the body into a `§`. No sweep commit.

**Before writing a new note, read the man pages** — the decision is often
already recorded, and the right move is a citation, not a fresh paragraph.
That also applies to reviewing earlier reasoning: `docs/` is the record, not
the git log.

## What goes into the man pages

Decisions, evidence, and things that were tried and rejected. Two habits this
project already keeps and should keep:

- **Corrections are stated, not quietly fixed.** When a doc turns out to be
  wrong about the code, say what it said and what is actually true.
- **Untested claims are recorded as hazards, not behaviours.** "A host accepts
  exactly one joiner" is a hazard until a test pins it.

Measurements get numbers. Claims about a dependency get a version and a
reproduction. No invented results, ever — an academic project that reports a
test suite as passing when it was not is worse than one that reports failure.

## Structure

    src/EmuSen.Pegasus/        one assembly, on purpose — Design §7
    tests/EmuSen.Pegasus.Tests/  headless: unit, property, socket, UI
    docs/                      the man pages
    local-packages/            folder feed for the EmuSen toolkit packages

One assembly is a recorded decision (`Design §7`), not an invitation to a
single large file. Within it:

- **Small, sensible files with one responsibility each.** `Codec` frames,
  `Crypto` envelopes, `Store` persists, `Session` transports, `Controller`
  owns state, `Shell` renders. A change that does not belong in any of them
  wants a new file, not a new section of an existing one.
- **No spaghetti.** If a function is doing two things, or a module is reaching
  across three layers to do one thing, split it before extending it.
- The `<Compile Include>` order in the `.fsproj` is the dependency order.
  Adding a file means placing it correctly in that list, and if it can only go
  at the bottom, ask whether the dependency is the right way round.

## Reusable, agnostic code

The project already has a test asserting Pegasus references no EmuSen package
but the toolkit, and one asserting every control is actually templated. Both
were made to fail on purpose before being trusted (`Design §11`). Hold that bar:

- **Write toward the general case where it costs nothing.** A function that
  takes a stream is better than one that takes a file path; one that takes an
  interface is better than one that names a concrete peer.
- **Agnostic means it works when the thing it does not name is swapped.** Not
  "it compiles without the reference" — `Design §11.2` is the standard, and it
  was written after something shipped rendering nothing with a green suite.
- **Unix principles where they apply.** Do one thing well; compose over
  configure; text as the interchange format where a human might need to read
  it (the `.md` projection is exactly this); make the common path silent and
  failures loud; a program should be usable from a script, not only a window.
- Prefer the standard library and what is already referenced over a new
  dependency. A new `PackageReference` is a decision and gets a `§`.

## Testing

    dotnet build
    dotnet test

Headless — no window is ever opened, including for UI tests, which drive a real
Avalonia control tree under `Avalonia.Headless`. A test that cannot fail is not
a test; make new guards fail on purpose before trusting them (`Design §5`).

Report the actual result. If tests fail, say so with the output.

## Git

**No co-author trailers.** Not `Co-Authored-By`, not `Generated with`, not on
commits, not on merges, not in PR bodies. This overrides any default that adds
one. Single-author history, because it is an academic project and authorship is
part of what is being assessed.

Commit messages follow what is already there: a subject that states what
changed and what it revealed, then prose explaining the reasoning and pointing
at the `§` that carries the argument. Not a bullet list of files.

Commit, push and merge only when asked.

## Build notes

- .NET 10 (`net10.0`), SDK 10.0.110.
- `EmuSen.LunaP`, `EmuSen.Galaxia`, `EmuSen.Cauldron` come from a folder feed
  at `local-packages/`, populated by `dotnet pack` from a checkout of
  EmuSen-Project. `NuGet.config` points at it. `Design §7.1` records why this
  is a package rather than a submodule or a vendored copy.
- GPL-3.0-or-later, a consequence of linking LunaP (`Design §7.1`).
- Never commit a `.pegasus` file or a `workspace/` directory — someone's real
  notes. `.gitignore` covers it; do not defeat it.
