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

Pegasus was built inside the [EmuSen](https://github.com/RedQuE3n/EmuSen-Project)
emulator project and moved here once it depended on nothing of EmuSen's but the
shared Avalonia toolkit.

## Layout

    src/EmuSen.Pegasus        document replica, storage, crypto, transport, UI
    tests/EmuSen.Pegasus.Tests  headless: unit, property, socket and UI tests
    docs/                     the reasoning; code comments point here

One assembly, on purpose. It arrived as four and `docs/Pegasus_Design.md` §7
records why the boundaries were not worth their cost.

## Building

Pegasus depends on `EmuSen.LunaP`, the shared Avalonia toolkit, which lives in
the EmuSen repository and is consumed here as a NuGet package. Until those
packages are on GitHub Packages, `NuGet.config` points at `local-packages/`, a
folder feed you populate from a checkout of EmuSen-Project:

    dotnet pack EmuSen.Cauldron/EmuSen.Cauldron.csproj -c Release -o <path>/Pegasus/local-packages
    dotnet pack EmuSen.Galaxia/EmuSen.Galaxia.csproj   -c Release -o <path>/Pegasus/local-packages
    dotnet pack EmuSen.LunaP/EmuSen.LunaP.csproj       -c Release -o <path>/Pegasus/local-packages

Then:

    dotnet build

`docs/Pegasus_Design.md` §7.1 records why this is a package rather than a
submodule or a vendored copy, and what the arrangement does not cover.

## Running it

    dotnet run --project src/EmuSen.Pegasus

Pegasus opens on a sign-in window. Pick a handle — `RedQuE3n`, the name your
peer will see you as — and a password, and click **Create**; after that,
**Sign in** with the same pair. The password unlocks a keypair kept on your own
machine and is never sent anywhere. `docs/Pegasus_Identity.md` §2 is worth
reading before you trust a handle: it is carried on the wire but not yet proven.

Then one person clicks **Host** and reads out the address, the port and the join
code. The other types all three in and clicks **Join**. The port is assigned by
the operating system and differs every session.

Notes live under `SpecialFolder.LocalApplicationData` — `~/.local/share/Pegasus/workspace`
on Linux — one `.pegasus` file each, with a plain `.md` projection beside every
note for reading in any editor. The `.md` is regenerated and never read back;
`docs/Pegasus_Format.md` §5 explains why.

Two limits worth knowing before you pair: a host accepts exactly one joiner, and
switching notes while connected is not supported — disconnect first.
`docs/Pegasus_Sync.md` §1 and §4 state both precisely.

## Tests

    dotnet test

85 tests, all headless — no window is ever opened, including for the UI tests,
which drive a real Avalonia control tree under `Avalonia.Headless`. The suite
covers frame and crypto round trips, torn-file recovery, compaction, caret
arithmetic, property-based convergence under randomised interleavings, identity
files and their password envelope, two peers over a real loopback socket, and
the full path from one peer's mailbox to the other's rendered editor.

Several of those tests are guards that were made to fail on purpose before being
trusted: two assert every control in a window is actually templated, one asserts
the assembly references no EmuSen package but the toolkit, and one asserts the
private key never reaches the identity file in the clear. The last of those
passed against a deliberately unsealed write on its first attempt — it compared
raw bytes to a base64 file — and `docs/Pegasus_Identity.md` §3 records the
correction. `docs/Pegasus_Design.md` §11 explains what shipped before the first
of them existed.

## Documentation

| Document | Contents |
|---|---|
| `docs/Pegasus_Design.md` | Why a CRDT, why F#, the Phase 0 evidence — including two YDotNet defects found and worked around — and why the toolkit arrives as a package |
| `docs/Pegasus_Format.md` | The `.pegasus` file, its recovery argument, and exactly what durability is promised |
| `docs/Pegasus_Sync.md` | Pairing, frame layout, and an honest account of what the encryption is not |
| `docs/Pegasus_Identity.md` | Handles, the password-sealed keypair on disk, and exactly what a sign-in proves |

Two findings there are worth flagging for anyone else building on `YDotNet`
0.6.0: the default `Doc()` constructor draws client ids from roughly six bits and
collides readily, and any client id at or above 2^32 causes `StateDiffV1` to
ignore the state vector and replicas to diverge. Both are demonstrated with
measurements in `Pegasus_Design.md` §4.5 and §4.7.

## Status

The core, the transport and the desktop client work. Remote presence is carried
on the wire but not yet drawn — `docs/Pegasus_Sync.md` §4. Handles are carried
but not yet proven; a signed handshake and pinned keys are the next pass —
`docs/Pegasus_Identity.md` §2 and §7.

Deferred by choice: an always-on relay (the session abstraction is shaped to
accept one as a peer that never disconnects), rich text, mobile, a browser
client, and LAN autodiscovery.

## Licence

GPL-3.0-or-later. Pegasus links `EmuSen.LunaP`, which is GPL-3.0, so this is a
consequence rather than a choice; `docs/Pegasus_Design.md` §7.1 records it.
