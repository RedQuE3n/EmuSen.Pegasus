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

Pegasus was built inside the [EmuSen](https://github.com/RedQuE3n/EmuSen)
emulator project and moved here once it depended on nothing of EmuSen's but the
shared Avalonia toolkit.

## Layout

    src/EmuSen.Pegasus.Core   wire types, frame codec, sealed envelope, identity keys
    src/EmuSen.Pegasus        document replica, storage, transport, UI
    tests/EmuSen.Pegasus.Tests  headless: unit, property, socket and UI tests
    docs/                     the reasoning; the code explains itself and cites here

Two assemblies. It arrived as four, was collapsed to one, and gave one boundary
back when `EmuSen.Chariot` — the server — needed to speak the same wire protocol.
`docs/Pegasus_Design.md` §7 records all three movements and the single rule
behind them. The core declares one dependency, `FSharp.Core`, and a test keeps
it that way.

## Building

    dotnet build

That is the whole of it. Pegasus depends on
[`EmuSen.LunaP`](https://github.com/RedQuE3n/EmuSen.LunaP), a small Avalonia
toolkit, and it comes from nuget.org like anything else.

It has not always been that simple, and the history is short enough to be worth
knowing. The toolkit lived inside an emulator project and had to be hand-packed
into a folder feed here, along with the two EmuSen assemblies it declared as
hard dependencies — three packages, none of them on a real feed, and a build
that failed for anyone who had not cloned the emulator first. The toolkit
stopped naming anything of EmuSen's, moved to its own repository, and is
published from a tag. `docs/Pegasus_Design.md` §7.1 records all of it.

`docs/Pegasus_Design.md` §7.1 records why this is a package rather than a
submodule or a vendored copy, and what the arrangement does not cover.

## Running it

    dotnet run --project src/EmuSen.Pegasus

Pegasus opens on a sign-in window. Pick a handle — `RedQuE3n`, the name your
peer will see you as — and a password, and click **Create**; after that,
**Sign in** with the same pair. The password unlocks a keypair kept on your own
machine and is never sent anywhere. `docs/Pegasus_Identity.md` §2 is worth
reading before you trust a handle: it is proven, and what that does and does not
establish is narrower than it sounds.

(This paragraph used to end "it is carried on the wire but not yet proven",
which stopped being true two passes ago and is corrected here rather than
quietly dropped.)

Then pair, one of two ways.

**Through a server**, if you run one — `EmuSen.Chariot`. Type its address and
passphrase into the buddy panel and click **Sign in**; everyone else signed in
appears in the list. Agree a join code between yourselves, type it into the box
at the top, pick each other out of the list and click **Open note**. No address,
no port, and the server is remembered for next time.

**Directly**, with nothing in the middle. One person clicks **Host** and reads
out the address, the port and the join code; the other types all three in and
clicks **Join**. The port is assigned by the operating system and differs every
session.

The join code is in both, and that is not an oversight. It is the key your notes
are sealed under, a server has no way to derive it, and a relay that could read
your notes would be a different program — `docs/Pegasus_Sync.md` §4.1. What a
server saves you is the address and the port, which are the two things that
changed every session.

Notes live under `SpecialFolder.LocalApplicationData` — `~/.local/share/Pegasus/workspace`
on Linux — one `.pegasus` file each, with a plain `.md` projection beside every
note for reading in any editor. The `.md` is regenerated and never read back;
`docs/Pegasus_Format.md` §5 explains why.

Two limits worth knowing before you pair: a direct host accepts exactly one
joiner, and opening another note drops whatever is connected, because a live
conversation holds the document that was open when it started.
`docs/Pegasus_Sync.md` §1 and §4 state both precisely; the second used to say
"disconnect first" and now does it for you.

## Tests

    dotnet test

150 tests, all headless — no window is ever opened, including for the UI tests,
which drive a real Avalonia control tree under `Avalonia.Headless`. The suite
covers frame and crypto round trips, torn-file recovery, compaction, caret
arithmetic, property-based convergence under randomised interleavings, identities
and pinned peer keys in SQLite, the routing envelope a relay reads without being
able to open what it carries, the mutual identity proof over a real loopback
socket including a refused impostor, two peers over that same socket, the startup
path from sign-in window to notepad through a real desktop lifetime, a note
opened with somebody by name through a relay with no address and no port typed
anywhere, and the full path from one peer's mailbox to the other's rendered
editor.

Several of those tests are guards that were made to fail on purpose before being
trusted: two assert every control in a window is actually templated, one asserts
the application references no EmuSen package but the toolkit and its own core,
one asserts the core carries neither Avalonia nor YDotNet into a server that
wants neither, and one asserts the private key never reaches the identity file
in the clear. The last of those
passed against a deliberately unsealed write on its first attempt — it compared
raw bytes to a base64 file — and `docs/Pegasus_Identity.md` §3 records the
correction. `docs/Pegasus_Design.md` §11 explains what shipped before the first
of them existed, and §12.4 lists the four sabotages the startup tests were held
against.

The buddy list arrived with five more, each watched failing first: dropping the
re-greeting that repairs a one-sided open, emptying the roster on its way to the
list box, remembering a server address before the connection worked, prefilling a
passphrase that is deliberately never stored, and switching notes without
dropping the connection holding the document. The pass that made the relay prove
itself added three more: skipping the signature on an ephemeral key, keeping
control traffic sealed under the passphrase, and believing whatever server turns
up.

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
on the wire but not yet drawn — `docs/Pegasus_Sync.md` §4. Handles are proven: each peer signs a
challenge with the key its fingerprint names, and a key that changes for a known
handle is refused — `docs/Pegasus_Identity.md` §2 and §7.

Connecting through a relay works, is tested, and **is now in the window**: sign
in to a server, pick somebody out of the buddy list, and share a note with them
by name. `docs/Pegasus_Sync.md` §4.1 is the transport; the server is
`EmuSen.Chariot`.

The relay proves itself to you: it presents its own key, signs a challenge with
it, and your client pins that key and refuses a change afterwards — the same rule
it applies to a person. The passphrase is a doorbell again rather than a key:
once both ends have proved themselves they agree an ephemeral session key, so
holding the passphrase no longer means being able to read everybody's roster off
the wire. `docs/Pegasus_Sync.md` §4.3 has the exchange and an honest account of
what it does and does not buy. One person may be signed in from two places at
once, and appears once in everybody's list.

Deferred by choice: rich text, mobile, a browser client, and LAN autodiscovery.

The relay is no longer among these, and the sentence that used to sit here —
"the session abstraction is shaped to accept one as a peer that never
disconnects" — turned out to be wrong for a reason worth reading:
`docs/Pegasus_Sync.md` §1.

## Licence

GPL-3.0-or-later. Pegasus links `EmuSen.LunaP`, which is GPL-3.0, so this is a
consequence rather than a choice; `docs/Pegasus_Design.md` §7.1 records it.
