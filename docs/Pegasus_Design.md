# Pegasus — design record

This file records decisions, the evidence behind them, and predictions that were
retired. Code comments in this repository stay to one line and point here; the
argument lives in prose, not in the source.

## 1. What Pegasus is for

A live notepad two people type into simultaneously while working. The governing
requirement, which arrived after an initial survey of existing tools and which
selects the entire architecture, is that **no party may lose information**.

Mature alternatives were surveyed before any code was written — CryptPad,
HedgeDoc, Etherpad, Rustpad, and Teamtype (the peer-to-peer editor-agnostic tool
formerly called Ethersync). The decision to build rather than adopt was taken
deliberately with that survey in hand. This section exists so that a later reader
does not mistake the build for ignorance of the alternatives.

## 2. Why a CRDT, and why not a simpler scheme

A last-writer-wins design loses data by construction: two people editing the same
region concurrently means one edit is discarded, and no amount of care in the
transport layer recovers it. Operational transformation preserves both edits but
requires a central authority to order operations, which reintroduces the single
point whose failure the requirement forbids.

A CRDT gives the property structurally. Every peer holds a complete replica,
concurrent edits merge deterministically, and a peer that has been offline
converges on reconnect without prompting anyone to choose a version. The
requirement is therefore not "use a CRDT because it is modern"; the requirement
is the CRDT restated.

Pegasus does not implement one. Writing a correct sequence CRDT is a research
project, and the failure mode of getting it subtly wrong is silent corruption of
the exact data the project exists to protect. It binds Yrs — the Rust
implementation of Yjs — through `YDotNet`.

## 3. Why F#, when F# was rejected on a sibling project

F# was evaluated on EmuSen's `F#ascent` branch and reverted in full. Those
objections were **boundary** objections and do not transfer to a standalone
codebase:

- The exhaustiveness benefit evaporated because C# gets no exhaustiveness
  checking over an F# union — a `switch` covering two of six cases compiles
  clean. Pegasus has no C# to be checked against; the wire protocol union is
  matched only from F#.
- `FSharp.Core` (2.4 MB) was pushed into every EmuSen frontend and the standalone
  shell. Pegasus has no host to burden.
- FsCheck gained nothing there because CsCheck matched it on generation and
  shrinking. Here FsCheck is the native choice, not a second testing story.

What F# is expected to earn, recorded so it can be checked rather than assumed:

1. `MailboxProcessor` as the answer to YDotNet's transaction discipline (§4.2).
2. Unions modelling the protocol with exhaustiveness that is actually enforced.
3. Property-based convergence testing as a first-class idiom.

If these do not materialise in practice, that is a finding to write down here,
not to paper over.

## 4. Phase 0 — dependency spike, run 2026-08-09

Nothing was built until the two young dependencies were proven on this machine.
Both spikes lived in a scratch directory and are not part of the repository; what
follows is what they established.

Environment: Fedora 44, .NET SDK 10.0.110.

### 4.1 YDotNet 0.6.0 — PASSED

`YDotNet` 0.6.0 and `YDotNet.Native.Linux` 0.6.0 restore on .NET 10 and the
`libyrs.so` native for `linux-x64` loads. Four claims were tested directly:

| Claim | Result |
|---|---|
| Divergent concurrent edits converge to identical text | PASS |
| Neither side's edit is lost in the merge | PASS |
| `ObserveUpdatesV1` yields a log that replays into an identical document | PASS |
| `StickyIndex` tracks a caret across remote edits | PASS |

The convergence test established a shared base, then had two replicas edit
without having seen each other, then exchanged state vectors and shipped only the
missing operations — 20 bytes one way, 18 the other. Both replicas ended
identical and contained both edits.

The `ObserveUpdatesV1` result is what makes the append-only file format in
`Pegasus_Format.md` viable: the observer's payload is exactly what must be
appended, and replaying the log reconstructs the document.

`StickyIndex` was tested in both directions. A caret at offset 3 moved to 8 when
five characters were inserted before it, and stayed at 8 when three were appended
after it.

### 4.2 Two API contracts discovered the hard way

Both cost a spike iteration and both shape `Document.fs`:

- **`Doc.Text(name)` throws if any transaction is open.** It defines the root
  type; `Transaction.GetText(name)` only fetches, and returns `null` before the
  root has been defined. The handle must therefore be acquired once, outside any
  transaction, and reused. `DocumentActor` holds the `Doc` and its root `Text`
  together for this reason.
- **The library actively throws on overlapping transactions**, with
  `"Failed to open a transaction, probably because another transaction is still
  open."` This is stronger evidence for the single-owner actor than the
  second-hand thread-safety report that originally motivated it — the constraint
  is enforced, not merely advised.

### 4.3 A prediction retired: Awareness

The plan was written believing YDotNet did not support the Yjs Awareness
protocol, and specified building presence as a bespoke message type. **That was
wrong.** The `YDotNet.Protocol` namespace ships the entire protocol:

- `SyncStep1Message`, `SyncStep2Message`, `SyncUpdateMessage`
- `AwarenessMessage`, `QueryAwarenessMessage`, `AwarenessInformation`
- `Encoder` / `Decoder` with varint framing, and read/write extensions

Recorded as a retired prediction because the plan's risk register named this as
an unknown and the answer turned out better than assumed.

**Revised by §4.6.** The consequence first drawn here — that presence would ride
the standard protocol types and Pegasus would therefore speak `y-websocket` on
the wire — did not survive. The serialisation problem in §4.6 pushed the frame
layout to a bespoke binary encoding. What survives is the weaker and still useful
claim: the *payloads* Pegasus carries are ordinary Yjs updates and state vectors,
so a bridge to a `y-websocket` client remains a translation shim at the frame
boundary rather than a change to the document model.

### 4.4 Avalonia.FuncUI 2.0.0 — PASSED

FuncUI 2.0.0 shipped 2026-07-28 and was twelve days old when adopted, which was
the largest scheduled risk in the plan. It restores against Avalonia 12.1.0, and
a `Component` with `useState`, a `DockPanel`, a `TextBox` and a `TextBlock`
compiled on the first attempt.

More usefully, it renders under `Avalonia.Headless`: the spike showed a
`HostWindow`, walked the tree, set `TextBox.Text`, pumped the dispatcher, and saw
the sibling `TextBlock` update through component state. Pegasus can therefore
test its UI without putting a window on anyone's screen.

One mechanism note for the test suite: **FuncUI builds no XAML name scope**, so
`FindControl<T>(name)` throws `"Could not find parent name scope."` Tests reach
controls through `GetLogicalDescendants()` instead.

The plain-Avalonia fallback specified in the plan was not needed and is retired.

## 4.5 A defect found by the property test: colliding client ids

Yjs identifies every operation by `(clientId, clock)`. Two replicas sharing a
clientId therefore mint colliding operation identities, and the merge silently
keeps one side's work and discards the other's. This is precisely the failure
Pegasus exists to prevent, so it is worth stating how close it came to shipping.

`YDotNet`'s parameterless `Doc()` does not draw a client id with anything like
the entropy Yjs assumes. Measured directly:

    2000 documents created -> 16 distinct client ids
    id 48 seen 138 times, id 0 seen 137 times, id 54 seen 121 times

That is roughly six bits. With two peers the collision probability per pairing is
about one in sixteen.

The pathology was then demonstrated rather than argued. Two replicas were forced
to share id 4242, each given a different edit, and synced in both directions:

    a="AAAA"  b="BBBB"   ->  after a full bidirectional sync:  a="AAAA" b="BBBB"

Each side kept only its own edit. No error was raised. `DocumentActor` therefore
always sets an explicit id, and three tests pin the behaviour, including one that
asserts the shared-id case still *diverges* -- if that test ever starts passing,
YDotNet changed and this section needs revisiting.

How it was found is worth recording: the hand-written convergence tests all
passed. The FsCheck property test failed intermittently, roughly one run in
twenty-five, which is what a one-in-sixteen collision looks like through a filter
of small cases. A hand-written suite would not have caught this.

## 4.6 System.Text.Json cannot serialise F# unions

`PeerId` and `NoteId` are single-case unions, and `JsonSerializer` throws
`NotSupportedException` on any F# union. Flat DTO records were tried first and
failed too: records nested in a module are not constructible by the deserialiser,
and `[<CLIMutable>]` did not rescue them.

The wire format is therefore binary -- a tag byte followed by
`BinaryWriter`-framed fields. This is smaller than JSON, has no dependency on a
serialiser's opinion of F#, and keeps the format entirely under our control. The
cost is that the frame layout is now something a human cannot read off the wire,
which is what `Pegasus_Sync.md` §3 exists to compensate for.

## 4.7 A second defect: client ids at or above 2^32 break delta sync

Fixing §4.5 by drawing ids uniformly below 2^53 -- the documented Yjs ceiling, so
a JavaScript peer can hold one exactly -- broke convergence *worse*, and in a way
that initially looked like a bug in our own mailbox.

It is not. With a client id at or above 2^32, `Transaction.StateDiffV1` ignores
the state vector it is given and returns the entire document:

    small ids (36, 22)        forB = 11 bytes of 19   forA = 11 bytes of 26   converged
    large ids (~10^15)        forB = 26 bytes of 26   forA = 41 bytes of 41   DIVERGED

The delta exactly equals the full state, which is the signature of a state vector
that failed to decode. Applying that full state then re-integrates operations the
receiver already held, producing a duplicated document: `"BASE-A"` and `"BASE-B"`
merged into `"BASE-BBASE-A"` rather than `"BASE-A-B"`.

The boundary was bisected and is exact:

    2^28, 2^29, 2^30, 2^31    converged
    2^32 - 1                  converged
    2^32 and above            DIVERGED

This is a 32-bit truncation somewhere in the binding's state-vector path.
`ClientId.ExclusiveMax` is therefore 2^32, giving 32 bits of entropy -- ample
against collision for a handful of peers, and far above the six bits the default
constructor supplies.

Two hypotheses were tested and rejected on the way, recorded so they are not
retried:

- **Native memory lifetime.** The suspicion was that byte arrays returned by
  YDotNet were views over memory freed when the transaction was disposed, and
  that the mailbox let them outlive it. Copying every array inside the
  transaction changed nothing.
- **Zeroed `DocOptions` fields.** Constructing `DocOptions(Id = ...)` might have
  silently defaulted the other options away from the library's intent. It does
  not: `DocOptions.Default` and `DocOptions(Id = 1)` agree field for field
  (`Encoding = Utf16`, `ShouldLoad = true`, the rest false/null), and building
  the options from `Default` reproduced the divergence identically.

The isolating step that mattered was reproducing the divergence with no
`MailboxProcessor` involved at all. Until that ran, the actor was the prime
suspect and the library was assumed correct.
