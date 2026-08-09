# Pegasus

A live notepad two people type into at the same time, built so that neither can
lose work.

Written in F#, cross-platform through Avalonia, and backed by a CRDT so that
concurrent edits merge rather than compete. Every peer keeps a complete replica
on its own disk; a peer that has been offline converges on reconnect without
anyone being asked to choose a version.

## Why it exists

Existing tools do this well — CryptPad, HedgeDoc, Etherpad, Teamtype. They were
surveyed first, and the decision to build anyway was deliberate. The requirement
that shaped the result is narrower than "a shared notepad": **no party may lose
information.** That single constraint chooses the architecture, and
`docs/Pegasus_Design.md` §2 explains why nothing simpler than a CRDT satisfies it.

## Layout

    src/Pegasus.Core     document replica, storage format, crypto, wire types
    src/Pegasus.Net      TCP session, handshake, sync protocol
    src/Pegasus.App      Avalonia desktop client (FuncUI)
    tests/Pegasus.Tests  headless: unit, property, socket and UI tests
    docs/                the reasoning; code comments point here

## Running it

    dotnet run --project src/Pegasus.App

One person clicks **Host** and reads out the port and the join code. The other
types both in and clicks **Join**.

Notes live in `~/.local/share/pegasus/workspace`, one `.pegasus` file each, with a
plain `.md` projection beside every note for reading in any editor. The `.md` is
regenerated and never read back — `docs/Pegasus_Format.md` §5 explains why.

## Tests

    dotnet test

52 tests, all headless — no window is ever opened, including for the UI tests,
which drive a real Avalonia control tree under `Avalonia.Headless`. The suite
covers frame and crypto round trips, torn-file recovery, compaction, caret
arithmetic, property-based convergence under randomised interleavings, two peers
over a real loopback socket, and the full path from one peer's mailbox to the
other's rendered editor.

## Documentation

| Document | Contents |
|---|---|
| `docs/Pegasus_Design.md` | Why a CRDT, why F#, and the Phase 0 evidence — including two YDotNet defects found and worked around |
| `docs/Pegasus_Format.md` | The `.pegasus` file, its recovery argument, and exactly what durability is promised |
| `docs/Pegasus_Sync.md` | Pairing, frame layout, and an honest account of what the encryption is not |

Two findings there are worth flagging for anyone else building on `YDotNet`
0.6.0: the default `Doc()` constructor draws client ids from roughly six bits and
collides readily, and any client id at or above 2^32 causes `StateDiffV1` to
ignore the state vector and replicas to diverge. Both are demonstrated with
measurements in `Pegasus_Design.md` §4.5 and §4.7.

## Status

The core, the transport and the desktop client work. Deferred by choice: an
always-on relay (the session abstraction is shaped to accept one as a peer that
never disconnects), rich text, mobile, a browser client, and LAN autodiscovery.

## Licence

Not yet chosen.
