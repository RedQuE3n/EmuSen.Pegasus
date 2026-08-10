# Pegasus — working agreement

A live shared notepad in F#/Avalonia, CRDT-backed, where no party may lose
information. **This is an academic project.** The reasoning is the deliverable
as much as the code, and it must survive somebody reading it critically.

## The man pages

`docs/` is the man pages. Four of them, section-numbered:

| Man page | Covers |
|---|---|
| `docs/Pegasus_Design.md` | Why a CRDT, why F#, Phase 0 evidence, dependency defects, assembly boundaries, testing discipline, the startup path |
| `docs/Pegasus_Format.md` | The `.pegasus` file, append-only log, compaction, durability, the `.md` projection |
| `docs/Pegasus_Sync.md` | Topology, pairing, frame layout, the exchange, what the encryption is and is not |
| `docs/Pegasus_Identity.md` | Handles, the identity store, the password envelope, the identity proof, trust on first use |

`README.md` is the front door for a reader who has not opened the code. It
summarises; it never becomes the place a decision is recorded.

## Code explains itself

**Notes go in the code, beside the thing they explain.** Write as much as the
reader needs. A comment that takes six lines to say why something is the way it
is, is six lines well spent — the reader is a person opening the file for the
first time, and sending them to another document to find out why is a cost paid
every single time the file is read.

    /// Fixed salt, and that is a real weakness. Both peers must derive the same
    /// key from the join code alone, with no round trip in which to agree on a
    /// random one, so the salt cannot vary. An attacker can therefore precompute
    /// against the whole 9,216-code space; the iteration count raises the cost
    /// of doing so without changing the shape of the problem. This is a
    /// pre-shared key for two people on a LAN, not a security boundary.
    /// Measured entropy and the full threat model: Pegasus_Sync.md §5.

Not this — the reader now has to go and find out what the consequences are:

    /// Fixed salt. Consequences in Pegasus_Sync.md §5.

Rules that follow from it:

- **Explain why, and what breaks if someone changes it.** The code already says
  what it does.
- **A `§` citation is an addition, never a substitute.** Cite the man page for
  the long version — the measurement, the alternatives tried — *after* the
  comment has already explained the thing on its own terms.
- Every `§` cited from code must resolve. Adding a citation to a section that
  does not exist is a broken reference; write the section.
- **Keep them true.** A comment that has drifted from the code it sits beside is
  worse than no comment, because it is believed.
- Don't narrate the obvious. `// increment i` is noise; a name is better than a
  comment where a name will do.

## What still goes in the man pages

Everything that does not fit beside code, and is worth keeping:

- **Measurements**, with numbers. The 2,000-document client-id sample, the
  bisection of the 2^32 boundary.
- **Defects found in dependencies**, with a version and a reproduction.
- **Alternatives tried and rejected**, and why — so they are not retried.
- **Guards made to fail on purpose**, and what each sabotage turned red.
- **Corrections**: what a document said, and what is actually true.

Three habits this project keeps and should keep:

- **Corrections are stated, not quietly fixed.**
- **Untested claims are recorded as hazards, not behaviours.** "A host accepts
  exactly one joiner" is a hazard until a test pins it.
- No invented results, ever — an academic project that reports a test suite as
  passing when it was not is worse than one that reports failure.

## Structure

    src/EmuSen.Pegasus.Core/   shared with Chariot: Types, Codec, Crypto, Identity
    src/EmuSen.Pegasus/        the desktop application
    tests/EmuSen.Pegasus.Tests/  headless: unit, property, socket, UI, startup
    docs/                      the man pages
    local-packages/            folder feed for the EmuSen toolkit packages

Two assemblies, and the split is a recorded decision (`Design §7`), not an
invitation to make more. **Nothing goes in the core that a headless server would
not want**: no Avalonia, no YDotNet, nothing of EmuSen. A test enforces it, and
a `PackageReference` added there is a decision about Chariot too. Within the
application:

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
but the toolkit and its own core, one asserting the core carries nothing a
headless server would refuse, and one asserting every control is actually
templated. All were made to fail on purpose before being trusted (`Design §11`,
`Design §7`). Hold that bar:

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
- `Microsoft.Data.Sqlite` **must** be accompanied by an explicit
  `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5 pin. Alone it resolves a transitive
  2.1.11 with a high-severity advisory. `Identity §3.2` records it; EmuSen pins
  the same pair. Audit with a `dotnet list package` vulnerability check.
- Never commit a `.pegasus` file or a `workspace/` directory — someone's real
  notes. `.gitignore` covers it; do not defeat it.
