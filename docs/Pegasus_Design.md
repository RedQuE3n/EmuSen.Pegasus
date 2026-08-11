# Pegasus — design record

Pegasus is a live shared notepad: two people type into the same document at once,
from different machines, and neither can lose work.

It was built inside the EmuSen emulator project, where it was the only F#, and
moved to its own repository once it stopped needing anything of EmuSen but the
windowing toolkit. §7.1 records how that dependency is carried across the
repository boundary, and §11.2 records the test that keeps the claim honest.

The on-disk format is in `Pegasus_Format.md`; pairing and the wire protocol are in
`Pegasus_Sync.md`. Code comments point here rather than explaining themselves.

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

Note that this argument does not generalise back to EmuSen. It is an argument
about a leaf with no C# callers, and the move to a separate repository has made
the leaf condition structural rather than a promise.

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

### 4.4 Avalonia.FuncUI 2.0.0 — PASSED, and later dropped anyway

FuncUI 2.0.0 shipped 2026-07-28 and was twelve days old when adopted, which was
the largest scheduled risk in the plan. It restores against Avalonia 12.1.0, and
a `Component` with `useState`, a `DockPanel`, a `TextBox` and a `TextBlock`
compiled on the first attempt.

More usefully, it renders under `Avalonia.Headless`: the spike showed a
`HostWindow`, walked the tree, set `TextBox.Text`, pumped the dispatcher, and saw
the sibling `TextBlock` update through component state. Pegasus can therefore
test its UI without putting a window on anyone's screen.

One mechanism note, kept because it cost time: **FuncUI builds no XAML name
scope**, so `FindControl<T>(name)` throws `"Could not find parent name scope."`
Tests reach controls through `GetLogicalDescendants()` instead — which is still
how they work, for a different reason, now that FuncUI is gone.

The plain-Avalonia fallback specified in the plan was not needed and is retired.
The spike's verdict on FuncUI stands and is not the reason it was later dropped;
§8 records that decision, which was about idiom count rather than about anything
FuncUI failed to do.

### 4.5 A defect found by the property test: colliding client ids

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
asserts the shared-id case still *diverges* — if that test ever starts passing,
YDotNet changed and this section needs revisiting.

How it was found is worth recording: the hand-written convergence tests all
passed. The FsCheck property test failed intermittently, roughly one run in
twenty-five, which is what a one-in-sixteen collision looks like through a filter
of small cases. A hand-written suite would not have caught this.

### 4.6 System.Text.Json cannot serialise F# unions

`PeerId` and `NoteId` are single-case unions, and `JsonSerializer` throws
`NotSupportedException` on any F# union. Flat DTO records were tried first and
failed too: records nested in a module are not constructible by the deserialiser,
and `[<CLIMutable>]` did not rescue them.

The wire format is therefore binary — a tag byte followed by
`BinaryWriter`-framed fields. This is smaller than JSON, has no dependency on a
serialiser's opinion of F#, and keeps the format entirely under our control. The
cost is that the frame layout is now something a human cannot read off the wire,
which is what `Pegasus_Sync.md` §3 exists to compensate for.

### 4.7 A second defect: client ids at or above 2^32 break delta sync

Fixing §4.5 by drawing ids uniformly below 2^53 — the documented Yjs ceiling, so
a JavaScript peer can hold one exactly — broke convergence *worse*, and in a way
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
`ClientId.ExclusiveMax` is therefore 2^32, giving 32 bits of entropy — ample
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

---

## 5. Testing discipline

Everything is headless, including the UI. `Avalonia.Headless` renders a real
control tree without a display, so the window under test is the window that
ships — no window is ever opened on anyone's screen. This is the rule EmuSen
holds for its emulator cores, carried over deliberately.

The property test earned its place immediately. Both defects in §4.5 and §4.7
were found by randomised interleavings, not by the hand-written cases, which all
passed. §4.5's collision rate of roughly one in sixteen is exactly what an
intermittent property failure looks like through a filter of small examples.

`Caret.adjust` is a pure function for the same reason: the rule for where a
caret belongs after the buffer changed underneath it is arithmetic, and
arithmetic should not need a window to test.

§11.1 is the counter-example that keeps this section from being self-
congratulatory: a green headless suite proved the control tree and said nothing
at all about whether anything was drawn.

---

## 6. The note format

Moved to `Pegasus_Format.md`, which holds the layout, the recovery argument, and
an exact statement of what durability is and is not promised. This section number
is kept so the numbering above and below it does not shift.

---

## 7. Why this is two assemblies, having been four and then one

Pegasus arrived as four projects — core, transport, application and tests — was
collapsed to one plus tests, and has since given exactly one boundary back. The
rule did not change; the facts under it did, twice, and both movements are
recorded here rather than only the latest.

**The rule.** A project boundary has to buy somebody separability they are
actually using.

**Why four became one.** Nothing outside Pegasus consumed its document model or
its transport, and no second frontend was planned over either. Four assemblies
bought layering that only Pegasus itself observed, and the layering survives as
file order inside a project: `Types` before `Codec` before `Crypto` before
`Document` before `Store` before `Workspace` before `Session` before the UI.
F#'s compilation order makes that ordering a compiler-enforced fact rather than
a convention, which was most of what the separate projects were providing.

This was the same reasoning that retired `EmuSen.Crystal` and
`EmuSen.Nehellania` on 2026-08-05, applied before the boundary was paid for
rather than after.

**Why one became two.** `EmuSen.Chariot` — the server, `Chariot_Design.md` —
speaks the Pegasus frame protocol, so it needs the wire types, the codec, the
sealed envelope and identity keys. That is the somebody the rule asks for. The
alternative was two implementations of a frame format in two repositories kept
in step by hand, with `Pegasus_Sync.md` §3 as the only thing holding them
together, and the first divergence discovered as a decode failure between two
machines rather than as a build error.

So `EmuSen.Pegasus.Core` exists and holds four files:

    Types      PeerId, Handle, PeerInfo, Frame, ProtocolError
    Codec      frame encoding
    Crypto     the sealed envelope, both KDFs, challenge and response
    Identity   keypairs, fingerprints, signing and verification

Everything else stayed in the application, and the list of what did not move is
the useful half: `Document` and its YDotNet dependency, because Chariot never
merges; `Store` and `Workspace`, because it holds no notes; `Session`, because
the envelope it will need is not designed yet (`Chariot_Design.md` §5) and
moving it before then would be guessing; `IdentityStore`, because reading a
private key off disk is a client's job and not a server's. `Identity.fs` was
split along exactly that line — keys in the core, the file format in the
application.

The result is that the package declares **one** dependency, `FSharp.Core`. A
test asserts the core references nothing of Avalonia, nothing of YDotNet and
nothing of EmuSen, because the value of the boundary is entirely in what it
refuses to carry, and that is the assertion that will notice when it stops
refusing.

### 7.0 What this cost, exactly

One test changed: `Pegasus references the toolkit and nothing else of EmuSen`
now also permits `EmuSen.Pegasus.*`. The plan for this pass claimed all 94 tests
would pass unchanged, and that claim was wrong — stated here rather than
quietly adjusted. It was wrong in an instructive direction: the test was not
defective, the fact it encoded changed, and a test that encodes a fact should be
expected to change when the fact does. The half of it that still bites — any
*other* `EmuSen.` assembly is still forbidden — is untouched.

The other 93 passed without being edited, which is what the claim was really
reaching for: no test needed rewriting to accommodate the split, only to record
it.

`FSharp.Core` is no longer an argument that has to be won. It was the objection
that sank EmuSen's `F#ascent` branch, and the move to a separate repository
settles it structurally: no EmuSen frontend and no core can link this
executable, because it is not in that solution any more.

`FSharp.Core` is no longer an argument that has to be won. It was the objection
that sank EmuSen's `F#ascent` branch, and the move to a separate repository
settles it structurally: no EmuSen frontend and no core can link this executable,
because it is not in that solution any more.

### 7.1 Why the toolkit arrives as a package, and the core leaves as one

Pegasus needs `EmuSen.LunaP` (§8) and LunaP must stay in EmuSen, where three
other projects consume it. Four ways to cross that boundary were considered:

- **Vendor a slice of LunaP into this repository.** Pegasus uses a small surface —
  `LunaApp.Configure`, `ToolWindow`, five `Ui.*` helpers and the theme — so this
  was cheap. Rejected because a vendored copy forks: the corrections that make
  LunaP worth depending on (§8) would stop arriving, and the whole argument for
  using a shared toolkit is that it accumulates them.
- **Git submodule of EmuSen-Project.** Rejected: it makes building a notepad
  require checking out an emulator, which gives up most of what the split was
  for. That objection has since dissolved on its own — LunaP has its own
  repository and a submodule of *it* would carry no emulator — but the package
  is still the better answer, for the reason in the last paragraph of this
  section: a package is a boundary, and a submodule is a way of pretending
  there isn't one.
- **Drop LunaP and use raw Avalonia.** Rejected on evidence rather than taste —
  §8 records that the hand-rolled bootstrap this would return to is exactly what
  silently dropped `UseX11` on Wayland.
- **Publish LunaP as a NuGet package.** Adopted.

`EmuSen.LunaP` is at **0.6.0**, restored from nuget.org like any other package.
It is the only package of the toolkit's this repository takes at all — a
correction twice over: it used to be three, and they used to be hand-carried.

LunaP declared `EmuSen.Galaxia` and `EmuSen.Cauldron` as hard dependencies, so
both had to be packed and copied here alongside it. They are gone because LunaP
stopped naming them — a settings seam replaced Galaxia's `ConfigFile`, and the
two files that genuinely knew about EmuSen, a console keyboard map and a
telemetry dashboard, moved to the projects that own those subjects. LunaP's
nuspec now declares Avalonia and nothing else, and tests on both sides of the
boundary assert it.

**And then the toolkit left EmuSen entirely**, to
<https://github.com/RedQuE3n/EmuSen.LunaP>, with its sixteen commits of history.
`docs/LunaP.md` §19 and §20 there are the account. Nothing changed here except
the version and where the `dotnet pack` is run — which is the point of having
crossed the boundary with a package in the first place: this repository never
depended on where the source was, only on the artifact.

This does not weaken LunaP's layering rule; it enforces it. That rule now says
LunaP may reference Avalonia and nothing else, and its purpose is to stop a
launcher acquiring an entire emulator by accident. A package cannot reach back
up into a core, so the constraint that was a comment in a `.csproj` is a
property of the artifact — and the rule tightened because the toolkit is going
to its own repository, where "can anybody outside resolve this" replaces "does
this cost a consumer a core" as the question being asked.

Two limitations, stated rather than discovered later:

- ~~The package feed is currently a folder~~ — **resolved.** `EmuSen.LunaP` is on
  nuget.org, published from a tag by a workflow that holds no credential at all:
  NuGet Trusted Publishing exchanges a GitHub OIDC token, proving which
  repository and which workflow file is running, for a key that lives minutes.
  `NuGet.config` here is now `<clear />` plus nuget.org, and a bare clone of this
  repository builds with `dotnet build` and nothing else. This limitation stood
  for the whole of the toolkit's life outside EmuSen and is the last thing the
  split was waiting on.
- ~~Every package involved is stamped `0.1.0` and stays there~~ — **resolved.**
  LunaP's published version comes from the git tag rather than from its csproj,
  so it cannot be written in two places and disagree with itself, and the tag is
  what triggers the publish. This repository moved 0.2.0 → 0.3.0, and later
  0.3.0 → 0.5.0, by editing one line.

  The trap the old wording described has not gone anywhere; it has only stopped
  being reachable by accident. NuGet still caches by id **and** version, so it
  bites the moment somebody consumes a package from a local folder while
  iterating on both repositories at once — repack at a version already restored
  and the build fails on code that was just written. Delete the cached folder
  under `~/.nuget/packages/`, or use a prerelease suffix that changes every pack.
  `EmuSen.Chariot`'s `NuGet.config` records it where somebody will hit it.

A limitation that used to be here and no longer applies: `EmuSen.Galaxia` ships
its catalogue schema as `.sql` files that do not travel in its package, which
mattered while Galaxia was one of the three packages carried here. It is not
carried any more.

A licence consequence follows and is worth naming: EmuSen is GPL-3.0, so the
packages are GPL-3.0, so Pegasus is a derivative work of them. This repository's
licence is therefore not a free choice while §8 holds.

`EmuSen.Pegasus.Core` now leaves by the same road it arrived on. Chariot is in
another repository and cannot resolve a `ProjectReference` either, so the core
is a package too — and it is published to nuget.org from a `core-v*` tag by
`.github/workflows/publish-core.yml`, the same arrangement the toolkit uses and
by the same mechanism: NuGet Trusted Publishing, which exchanges a GitHub OIDC
token proving which repository and which workflow *file* is running for a key
that lives minutes. No credential is stored in this repository.

The tag is `core-v*` rather than `v*` on purpose. This repository holds two
things and only one of them is a package; the desktop application may want
releases of its own one day, and a tag namespace decided after the fact is a tag
namespace that collides.

The version comes from the tag rather than from the `.fsproj`, for the reason
the limitation below records: a version written in two places eventually
disagrees with itself, and NuGet's caching turns that disagreement into a build
failing on code that was just written.

**Publishing from CI caught something a local `dotnet pack` never would have.**
`FSharp.Core` was an implicit reference — the SDK adds it automatically at
whatever version *that* SDK ships — so 0.2.0, built on a runner, declared a floor
of `10.1.302`, where the identical source packed on the development machine
declared `10.0.110`. The published artifact varied by who built it, which is not
a property a package is allowed to have, and the first thing to notice was
Chariot restoring it and reporting an NU1605 downgrade against a package it had
just been told to trust.

It is pinned explicitly now, and pinned *low*: a floor is a demand on every
consumer, and nothing in the core uses anything newer. That is still one
dependency, which is what §7's rule and the test in `CoreTests` are about —
pinning changed the version, not the count.

**CORRECTION.** This section said "0.2.1 is the corrected package". That was
wrong, and it was published as well as written. 0.2.1 shipped the identical
defect it was released to fix, declaring exactly the `10.1.302` floor 0.2.0 had.

The explicit `PackageReference` was only half the pin. The F# SDK does not skip
its implicit `FSharp.Core` reference when it finds an explicit one — it adds a
*second* `PackageReference` for the same id from `Microsoft.FSharp.NetSdk.props`,
and NuGet resolves two entries for one id by taking the higher. The condition
guarding that props file is `DisableImplicitFSharpCoreReference` alone, which was
never set. `0.2.2` sets it, and is the first package whose nuspec carries one
`FSharp.Core` entry at `10.0.110`.

**Why it was verified and still wrong**, which is the part worth keeping. On this
machine both entries were `10.0.110`, because the SDK here ships the version that
was pinned. The duplicate therefore resolved to the right answer, the locally
packed nuspec was correct, and the check performed was a check that could not
have failed. The versions only differ on a runner — the same place the original
defect was only visible. *A pin that holds only where the two versions already
agree is not a pin, and confirming it against an artifact that could not have
disagreed is not a confirmation.*

**A second correction, about the tests.** This section previously credited "the
test in `CoreTests`", and the README claimed the core "declares one dependency,
`FSharp.Core`, and a test keeps it that way". No such test existed. The three
guards in `CoreTests` read `GetReferencedAssemblies` on a *built assembly*; a
package's dependency list is a different artifact, and nothing was reading it.
The claim was a hazard described as a behaviour for the whole of 0.1.0 through
0.2.2. It is a behaviour now, and both guards were made to fail on purpose
before being trusted (§5):

- `the core declares one dependency, and declares it itself` asks MSBuild for the
  `PackageReference` items the core *evaluates to*, not the ones written in the
  file, because the defect was an item nobody wrote. It asserts the count and the
  *definer* rather than the version — the count and the definer differ on every
  machine, where the versions differ only on some, so it fails here as well as on
  a runner. Sabotaged both ways: restoring the implicit reference turns it red
  with two `FSharp.Core` entries at the same version, and removing the explicit
  one turns it red with the reference attributed to `Microsoft.FSharp.NetSdk`.
- `publish-core.yml` opens the `.nupkg` it is about to upload and compares what it
  declares against what the project asked for, failing the job before the push.
  A push is irreversible, so the check that matters is the one before it, and
  there was nothing there when 0.2.0 and 0.2.1 went out.

`0.2.0` and `0.2.1` cannot be removed — published versions do not come back —
and both still work. They merely ask for more than they need, but "merely" is
doing real work in that sentence: a consumer pinning `FSharp.Core` at `10.0.110`
gets NU1605 from either, which is how Chariot found this in the first place.
Both were confirmed to still do it, by restoring each in turn from nuget.org
into a scratch consumer and reading the warning back.

**They should be unlisted, and that is outstanding.** Deletion is not available
and unlisting is — a distinction the first version of this paragraph missed by
reasoning from "cannot be deleted" to "must be left alone". Unlisting keeps them
resolvable for anything that already pins them while taking them out of search
and out of floating resolution, which is the right end state for two versions
whose only distinguishing feature is a defect. It is an action on the nuget.org
account, not a change in this repository: nothing here can perform it, because
the publishing workflow deliberately holds no long-lived credential, and a key
able to unlist would undo that.

Inside this repository the application takes a `ProjectReference` rather than
the package. A packing step between editing a frame and running the tests would
be paid on every change and would buy a version number nobody in this repository
reads. The package exists for the consumer that cannot see the source; the
project reference exists for the one that can.

---

## 8. Built on LunaP

The window is a `LunaP.Windowing.ToolWindow`, the layout comes from
`LunaP.Fluent.Ui`, and the process starts through `LunaApp.Configure`. Pegasus
therefore inherits the shared theme, remembered window geometry, and the
bootstrap sequence.

That last one is not cosmetic. `LunaApp.Configure` ends with `UseX11()` on Linux
because **`UsePlatformDetect` does not pick X11 on a Wayland session**. Pegasus
was first written outside EmuSen with a hand-rolled
`UsePlatformDetect().WithInterFont().LogToTrace()`, which reproduced three
quarters of `LunaApp` and silently dropped the part that matters on the machine
it was being written on. A shared toolkit earns its keep exactly here: not in the
controls it supplies, but in the corrections already encoded in it.

That history is also the reason §7.1 rejects vendoring. Pegasus has now been
outside EmuSen twice; the first time it reproduced this bug, and a fork of LunaP
is the arrangement that would let it happen again.

The first draft also used `Avalonia.FuncUI`, a declarative F# layer over
Avalonia. It was dropped in the move into EmuSen. FuncUI is pleasant and it
worked (§4.4), but a second UI idiom inside a repository that already had one is
a cost paid by every future reader, and nothing in a notepad needed what it added
over `Ui.Row` and `Ui.Dock`.

The premise of that decision has now partly expired — this repository has no
other UI idiom to be consistent with. The decision stands anyway, on the
remaining half of the argument: `Ui.*` and `ToolWindow` come from the dependency
Pegasus already has, and FuncUI would be two more packages to earn their place.
Recorded because a decision whose original reason has lapsed should be re-argued
in the open, not inherited silently.

### 8.1 Two-way binding wants a re-entrancy guard

The note list and the editor both read from and write to the same state, and the
first version deadlocked the dispatcher on open. `refreshNotes` set
`SelectedIndex`, which raised `SelectionChanged`, whose handler refreshed the
editor, which refreshed the list. `Dispatcher.RunJobs` drains jobs queued while
it is draining, so it never returned.

The fix is two flags — `applying` for the editor, `syncingSelection` for the
list — and a narrower `pullText` that deliberately does **not** touch the note
list. The general rule, and it applies to any LunaP window doing two-way
binding: a control being rewritten from state must not be able to report that
rewrite back as a user action.

Worth recording that the headless UI test caught this and manual clicking would
not have: a human opening the window sees it hang and calls it slow, whereas the
test hangs a build.

### 8.2 The buddy list is a control, not another row

`Buddies.BuddyList` is its own `UserControl` rather than a fourth section of
`Shell`, because it has its own state — the roster, the server address, who is
selected — and `Shell` already owns the notes and the editor. The working
agreement's rule is that a change which does not belong in any existing file
wants a new file; this is that rule applied rather than quoted.

Two seams in it are worth naming, because both were chosen to keep the control
testable without a database:

- **The join code arrives as a `unit -> string`, not a `TextBox`.** There is one
  join code in the window, in the top row, and both ways of pairing read it from
  there. Two boxes both labelled "join code" would be a genuinely confusing
  window and an easy mistake to make while pairing. Passing the getter rather
  than the control also means `BuddyList` cannot write to a control it does not
  own.
- **Remembered servers arrive as a `ServerBook`** — two functions, `Recent` and
  `Remember` — rather than a path to `identity.db`. A control that takes a path
  can only be tested with a database, and the headless suite has no business
  writing to whoever is running it. `Servers.forgetful` is what the tests use and
  `Servers.bookFor` is what `Program` passes. This is §11.2's standard applied to
  storage rather than to rendering: the control works when the thing it does not
  name is swapped.

The window exposes the panel as a member so a test can assert what is *shown*
rather than what the controller believes — a roster that reaches `Notepad.Roster`
and never reaches the list box is the same class of bug as the blank window in
§11, and the same kind of assertion catches it.

---

## 9. Why the tests are a separate project

`EmuSen.WiseMan` is that repository's headless harness and was the natural home
while Pegasus lived there, but WiseMan is C# and these tests are F#. The
convergence property is the point of them, and CsCheck — which matched FsCheck
well enough to help kill the `F#ascent` branch — cannot express a generator over
an F# document model without the model being C# in the first place.

So `EmuSen.Pegasus.Tests` exists, and it is the second and last name Pegasus
spends. It contains no harness of its own: it is xunit and FsCheck over the same
public surface the application uses.

The original reason is now historical — there is no C# harness in this repository
to have folded into — but the separation is kept, because a test project is the
one boundary that does buy separability: it is what lets §11.2 reflect over the
application assembly's references from outside it.

---

## 10. Sync

Moved to `Pegasus_Sync.md`, which holds the topology, pairing, frame layout, the
exchange, and an honest account of what the encryption is not. This section number
is kept so the numbering above and below it does not shift.

---

## 11. The blank window, and what "agnostic" has to mean to be worth anything

### 11.1 It shipped rendering nothing, and the suite was green

The first published build opened a white rectangle. Every control existed, the
window sized correctly, the socket layer worked, and 53 tests passed.

The cause is one missing line. Every EmuSen `App` includes
`avares://EmuSen.LunaP/Theme/LunaTheme.axaml`, which is `FluentTheme` plus the
shared palette; `Mistress` does it in `App.axaml` and `Hotaru` the same way.
Pegasus, ported from a standalone repo where it had added `FluentTheme` by hand,
overrode `Initialize` **not at all** after the move. Without a control theme,
`TextBox` and `ListBox` have no `Template`, and a control with no template
occupies layout and draws nothing.

**The suite missed it for a reason worth stating.** `LunaTheme.axaml` carries a
comment predicting exactly this — *"the one WiseMan's TestAppBuilder includes
too — a headless render pass that misses the theme silently asserts over
untemplated controls."* The UI tests built their headless `Application` with a
bare `FluentTheme()` instead of LunaP's theme. Every assertion walked the
**logical** tree, which is fully populated whether or not anything is
templated, so the tests were structurally incapable of seeing the defect and
were also loading a different theme than the application. Both halves were
wrong in the same direction.

Two changes, and the second matters more than the first:

- `Shell.applyTheme` is now the single place the style is loaded, and both the
  application and the tests call it. They can no longer diverge.
- A guard asserts every `TemplatedControl` in the window has a `Template` after
  measure and arrange, and it was **made to go red on purpose**: with the theme
  removed it fails naming `TextBox, ListBox`, which is the shipped symptom in
  words. The rule about guard tests in EmuSen's
  `EmuSen_Debugging_Tools_Reference_v5.md` §3.53 applies here exactly — the green
  was worth nothing until the red existed.

The general lesson for any LunaP consumer: a headless test that queries the
logical tree proves the tree, not the rendering. If what can break is *whether
anything is drawn*, the assertion has to be about templates or pixels.

This defect is also the reason §7.1 treats the theme as part of the package
contract rather than an incidental resource. The `avares://EmuSen.LunaP/` URI
resolves out of the packaged assembly, and the template guard is what would catch
it if a future package ever shipped without its compiled `.axaml`.

### 11.2 What agnostic means here

Pegasus is a notepad built on a windowing toolkit. It is not part of the
emulator, and `EmuSen.LunaP` is intended to be usable on its own as a general
Avalonia toolkit. Both statements are only true while Pegasus depends on LunaP
and on nothing else of EmuSen's.

That is a test rather than an intention: `Pegasus references the toolkit and
nothing else of EmuSen` reflects over the assembly's own references and fails
naming anything `EmuSen.*` other than `EmuSen.LunaP`. It reads direct references
deliberately — LunaP's own dependencies on Galaxia and Cauldron are LunaP's
business.

When that test was written, it ended "and will be settled when it is packaged".
That has now happened (§7.1), and the test passed across the move unchanged,
which is the useful result: the reference graph the test describes was already
correct, and packaging revealed no hidden edge into EmuSen. A test that has to be
rewritten to survive a repository split was not testing what it claimed.

Agnostic also means the three RIDs the project publishes are real targets rather
than aspirations, and one defect was found by taking that seriously:
`defaultWorkspaceRoot` was `~/.local/share/pegasus/workspace`, built from a
literal `.local/share`. That is a Linux convention, wrong on Windows and
unidiomatic on macOS, in a project whose `out/` has carried `osx-arm64`,
`osx-x64` and `win-x64` since before Pegasus existed. It resolves through
`SpecialFolder.LocalApplicationData` now.

Changing that path could have stranded an existing workspace, so it does not: an
existing directory at the old location keeps being used. This mirrors EmuSen's
`ConfigStore` Directory / PreviousDirectory / LegacyDirectory order rather than
inventing a second migration idiom — and it is the same rule that project's ROM
library gets, which is that data a user authored is read, never quietly
abandoned.

---

## 12. Testing the startup path

The window swap — sign-in window first, notepad replacing it — lived in
`Program.fs` and was for one commit exercised only by launching the application
and looking at it. That is not nothing, but it is not a test either, and this
section records closing the gap because *why* it was open is the interesting
part.

### 12.1 It was untestable because it named the real directories

`App` resolved `IdentityStore.defaultRoot` and `defaultWorkspaceRoot` itself.
Any test driving it would have created identities and notes in the developer's
own `~/.local/share/Pegasus`, so no such test could be written that was safe to
run. The fix is the general one this project keeps reaching for: take the thing
rather than name it. `App(identityRoot, workspaceRoot)` is what the suite
constructs, and a parameterless overload supplies the real pair because
`LunaApp.Configure<App>()` needs one.

This is the same shape as `Notepad` taking a `PeerInfo` instead of reaching into
a credential store (`Pegasus_Identity.md` §3). A component that names its own
inputs cannot be placed anywhere else, including a test harness — untestable and
un-reusable turn out to be the same property seen from two sides.

### 12.2 What the test drives, and the one thing it fakes

A real `App`, a real `ClassicDesktopStyleApplicationLifetime`, real windows,
and the real `OnFrameworkInitializationCompleted`. No stand-in for the code
under test, for the reason §11 exists.

Exactly one thing is supplied by the test that the framework would otherwise do:
`MainWindow.Show()`. `ClassicDesktopStyleApplicationLifetime.Start` shows the
main window and then runs a message loop; the suite must not run a message loop,
so it performs the first half and leaves the second. That is the honest boundary
of this test, and it is stated here rather than left for a reader to discover.

### 12.3 A second UI test class broke the first one

Adding `StartupTests` alongside `UiTests` immediately turned eight passing tests
red with `The calling thread cannot access this object because a different
thread owns it`.

xunit parallelises across test classes. Avalonia's dispatcher belongs to the
thread that set the platform up, for the life of the process. One UI test class
had concealed this: there was nothing to run in parallel with. Both now join an
`Avalonia` collection, which is xunit's way of saying "these run in sequence",
and the non-UI tests keep their parallelism. The suite was then run five times
over to confirm it, since a threading fix that happens to pass once is not a fix.

The shared bootstrap and the control-finding helpers moved to `Headless.fs` at
the same time, so the two classes share one platform setup rather than each
having an opinion about it.

### 12.4 Four sabotages, four reds

Every assertion here was watched failing against a deliberately broken build
before being trusted:

| Sabotage | Turned red |
|---|---|
| never assign the new `MainWindow` | 4 tests, including the swap itself |
| never close the sign-in window | the swap test, on the leftover window |
| construct the notepad eagerly, before sign-in | the two tests asserting an untouched workspace |
| drop both `desktop.Shutdown()` calls | the two tests asserting the process ends |

The third is worth keeping. "No workspace is touched before anyone has signed
in" reads like a nicety, and it is really the assertion that an unattended
machine sitting at the prompt writes nothing to disk and opens no note as
nobody. It is cheap to break by moving one line, and nothing else in the suite
would have noticed.

### 12.5 What is still not covered

The message loop. Nothing here proves the application survives a real user
session, only that the startup sequence assembles the right windows and asks to
exit at the right times. A launch by hand remains the only evidence for the
loop itself, and that is a reasonable place to stop: the loop is Avalonia's, not
ours.

## 13. Accessibility, and the padding that came with it

Two passes over the same three window files, done together because both are
about what the window is like to be in rather than what it does.

### 13.1 The measurement

LunaP's own accessibility work (`LunaP.md` §24) found that nine of the toolkit's
controls were not in the automation tree at all. That prompted the same question
here, and the same method answered it: `ControlAutomationPeer.CreatePeerForElement`
is the route a screen reader's platform bridge takes, so a probe can walk a real
window and print what assistive technology would find.

Every control the keyboard can land on, with the name it announces:

```
=== SignInWindow ===          === PegasusWindow ===
TextBox   name=""             Button  name="Host"          TextBox name=""
TextBox   name=""             TextBox name=""              Button  name="Sign in"
Button    name="Sign in"      TextBox name=""              Button  name="Sign out"
Button    name="Create"       TextBox name=""              Button  name="Open note"
                              Button  name="Join"          TextBox name=""
-> 2 of 4 named               Button  name="Disconnect"
                              TextBox name=""              -> 8 of 16 named
                              Button  name="+"
```

**Ten of twenty tab stops announced as nothing at all**, and every one of the ten
that did announce was a button reading its own caption back. Nothing in either
window had been named; the ten were free.

Three of them are worth pulling out.

**The editor announced as "edit".** The control the entire application exists
for — a shared notepad — had no accessible name and, unlike every other box in
the window, not even a placeholder to fall back on. A user arriving on it by
keyboard was told it was an edit field and nothing else.

**The "+" button announced as "+".** It has a name, so it does not appear in the
unnamed count, which makes it the most misleading row in the table: "plus" is
not something a person can act on.

**Eight text boxes relied on a placeholder that is not a label.** Every box
carries a good `PlaceholderText` and it is easy to assume that is the label. It
is a separate automation property, announced separately where it is announced at
all, and it disappears the moment the user types a character — which is exactly
when they might want reminding what the box was for.

### 13.2 What the fix is

LunaP's `.AccessibleName(…)`, `.HelpText(…)` and `.LiveRegion()`:

    let handle = TextBox(PlaceholderText = "handle").AccessibleName("Handle")

**This was `Access.fs` for one version, and that file is now deleted** — which is
the plan it was written with, stated in its own header. It was three F# functions
over `AutomationProperties`, and it existed only because this repository was on
LunaP 0.3.0 while those helpers arrived in 0.5.0. `AutomationProperties` is plain
Avalonia and works on both, so the accessibility of this application never had to
wait on a package bump. §13.7 records what happened when the bump came.

**The placeholders all stayed.** Both properties are set on every box, usually to
similar words, because they do different jobs.

**The editor takes the open note's name** — "Note: groceries" — so switching
notes is not silent. This is an application whose whole subject is having several
notes, and one name for all of them would have been the shape of the problem
rather than a fix for it.

**The "+" keeps its caption and gains a real name.** It is the only control here
whose accessible name differs from what it says on screen, and the reason it is
allowed to is that the caption is a symbol rather than a word: an accessible name
that drops the visible label normally breaks voice control, but there is no
"click plus" to match against in the first place.

**Two lines announce themselves**: the window's status line and the buddy panel's
message line. Both carry state in a **colour** — green for connected to a person,
goldenrod for merely signed in to a server, and `showStatus`'s own comment says
that distinction is the one thing a user must not be wrong about. A colour is
precisely what a screen reader cannot pass on. The wording differs too, so colour
was never the sole carrier, but only a user who goes and reads it would find out;
`Polite` is what makes it arrive. Polite and not `Assertive` because a region
that interrupts on every update makes an application unusable with a reader
running, which is a worse failure than the silence it replaces.

### 13.3 A defect the tests found and the screen did not

The first version renamed the editor inside `refreshNotes`, which looked right
and was not. `refreshNotes` runs when the window is built and when a note is
created. **Selecting an existing note in the list goes through neither**, so the
editor's name stayed on whichever note happened to be open first, and switching
notes announced the wrong one indefinitely.

Nothing on screen would have shown this — the text changes, the selection
changes, everything looks correct — and it was caught only because the test
switches notes *through the list*, the way a user does, rather than by calling
the controller. It is now a `nameEditor` function called from both places a note
can become the open one.

### 13.4 The padding

Nothing in the main window had a margin. Five docked regions met each other and
the window frame with no gap at all: the connection row sat on the window edge,
the note list touched the editor, the buddy panel touched the other side of it.
The window read as one dense block rather than four areas doing different jobs.

The gaps are asymmetric on purpose — **12 against the window frame, 8 between
neighbours** — so the outer edge reads as the boundary it is and the interior
seams read as lighter than it. The top and bottom strips carry a smaller gap
towards the middle (6) than towards the frame (10), which keeps them attached to
the work area rather than floating in it. The editor takes no margin of its own:
it is surrounded on all four sides by things that carry one, and a fifth would
open a double gap everywhere they meet.

Inside the panels, spacing now says which controls belong together rather than
being uniform. Address, port and passphrase sit at 6 inside a group separated
from its neighbours by 10; handle and password do the same at 6 against 14. A
form spaced evenly throughout reads as an undifferentiated column, and grouping
is the cheapest thing that fixes it.

The sign-in window grew from 420x330 to 460x400. At the old size the hint at the
bottom was already close, and 24 of margin plus the wider gaps would have clipped
it. `ToolWindow` remembers geometry per `WindowKey`, so this is the size of a
first run and not an override of anybody's saved one.

### 13.5 The guards, and seven sabotages

`tests/EmuSen.Pegasus.Tests/AccessTests.fs`, nine tests. The suite is **160**.

| Sabotage | Turned red |
|---|---|
| The editor's name removed | `the editor announces which note is open`, `the editor renames itself when another note is opened` |
| The "+" button's name removed | `the add button still says plus and announces what it does` |
| The status line's live region removed | `the status line and the buddy message announce themselves` |
| The note list's name removed | `every list says what it is a list of` |
| The passphrase box's name removed | `nothing the keyboard can reach in the main window is unnamed`, `every text box carries a name as well as its placeholder` |
| The sidebar's margin set back to zero | `the docked regions do not touch each other or the window frame` |
| The handle box's name removed | `nothing the keyboard can reach in the sign-in window is unnamed` |

**One of these guards could not fail when it was first written, and that is the
more useful finding.** `nothing the keyboard can reach is unnamed` walks the
window for focusable, visible tab stops. The first draft measured and arranged
the window but never *showed* it — and `IsEffectivelyVisible` is false for every
control in a window that was never shown, so the walk found nothing and the
assertion passed by having nothing to check. §5's rule is that a test which
cannot fail is not a test, and a test that passes because its subject is empty is
the version of that which looks healthiest from the outside. Both window guards
now assert the tab-stop **count** before asserting the names, which is what stops
it coming back.

The padding is asserted rather than eyeballed, because a margin is exactly the
kind of thing that silently returns to zero when somebody rebuilds a layout. Not
as a pixel baseline — §11 is clear those are an artefact of one machine — but as
the thing that was actually wrong: no docked region may have a zero margin,
except the editor, which is the fill child and is bounded by its neighbours.

### 13.6 What is not covered

**No screen reader has been run against this.** Every measurement is of Avalonia's
automation tree, which is what a platform bridge reads; it is not Orca or NVDA
reading it aloud. Being in the control view is necessary and is not the same as
verified end to end.

~~**The LunaP controls in these windows are still only as good as 0.3.0.**~~ —
**resolved.** The bump happened; §13.7 records what it changed here, which is
less than it changed in EmuSen and for a reason worth knowing.

**`ToolTip` is still unused**, and no control here has an explicit `TabIndex`.
Tab order follows the visual tree and reads correctly in both windows, so there
was nothing to reorder; it is recorded because a reader will want to know it was
checked rather than forgotten.

### 13.7 The 0.5.0 bump, and why it bought this repository almost nothing

One line in the `fsproj`, 0.3.0 → 0.5.0, skipping 0.4.0. **160 tests green on the first run, and not one accessibility assertion changed.** Both window guards assert an exact tab-stop count — 16 in the main window, 4 in sign-in — and both counts, and every name behind them, are identical on the two versions.

That is worth recording precisely because it is the opposite of what happened next door. EmuSen took the same bump and **twenty-six controls became reachable to a screen reader with no application change at all**, 69 of 97 named to 95 of 97 (`EmuSen_LunaP.md` §7.1). The same toolkit release, the same kind of measurement, and effectively zero here.

**The reason is what each repository uses LunaP for, and it is not a criticism of either.** Counting what Pegasus actually instantiates:

    9  Ui.Button      6  Ui.Hint    5  Ui.Stack   5  Ui.Row
    4  ToolWindow     2  Ui.Header  1  Ui.Dock

Every one of those is a window base class, a layout panel, or a `TextBlock` subclass. **Pegasus takes LunaP for window scaffolding, theming and layout, and builds its actual controls from raw Avalonia.** Nothing in these two windows is a `MeterList`, a `PathPickerRow`, a `FieldRow`, a `ConsolePane` or a `LunaSwitch` — which is the entire list of what §24 gave automation peers to. EmuSen's `DebugSettingsWindow` gained fifteen names in one go because fifteen of its sixteen tab stops are `LunaSwitch`es; Pegasus does not own a single one.

**The general form is worth keeping: the value of a fix in shared chrome scales with how much of the chrome you actually took.** A consumer that uses a toolkit for its window class and its palette gets a light column and a base class out of a bump, and nothing else, because there is nothing else of the toolkit's on screen to improve. That is a correct outcome rather than a disappointing one — and it is the reason the accessibility work here had to be done by hand in §13.2 while EmuSen's largest single win arrived for free.

**What the bump did buy: a deleted file.** `Access.fs` existed because this repository was on 0.3.0 and LunaP's `.AccessibleName(…)`, `.HelpText(…)` and `.LiveRegion(…)` arrived in 0.5.0. Its own header said it should forward to them or go away once the bump happened, and it has gone away — the three windows call LunaP's extensions directly now.

Deleting it was a choice rather than a necessity, and the check that made it a choice is worth stating: **F# resolves C# extension methods**, so `TextBox(…).AccessibleName("Handle")` compiles here exactly as it does in EmuSen. Keeping the F# pipeline form would have been defensible on ergonomics. What decided it is §9's rule about one vocabulary — the fluent surface names things after the XAML attributes they set so that code and markup stay one language, and a repository saying `Access.named` while the toolkit, its documentation and the other consumer all say `.AccessibleName` is a second vocabulary for one idea. The same argument that replaced EmuSen's hand-rolled path row (`EmuSen_LunaP.md` §7.2), applied to something much smaller.

Three lines needed parenthesising on the way — `Ui.Hint "" |> Access.live` became `(Ui.Hint "").LiveRegion()`, because without the parentheses F# reads the member access as binding to the string argument rather than to the result. The compiler caught all three; it is noted only because it is the one way this conversion can go quietly wrong in F# and not in C#.

---

## 14. The relicence, and a term that was inherited twice

This repository is **MIT**, and this is the first time its licence has been a choice.

§7's account of the package boundary ends with a paragraph naming the consequence: *"EmuSen is GPL-3.0, so the packages are GPL-3.0, so Pegasus is a derivative work of them. This repository's licence is therefore not a free choice while §8 holds."* That was true and correctly reasoned when it was written. It is no longer true, and not because §8 stopped holding — Pegasus still builds its window, its theme and its bootstrap on LunaP, exactly as §8 describes. It stopped being true because **LunaP relicensed to MIT**, on the finding that its own GPL was inherited from EmuSen rather than chosen, and that nothing in its dependency tree required it. `docs/LunaP.md` §25 in that repository is the account.

So the term arrived here by inheritance twice over: EmuSen chose it, LunaP carried it out of EmuSen without re-deciding it, and this repository took it from LunaP as a consequence it correctly identified as not a choice. Three projects, one decision, made once by the only one of them that is an emulator.

### 14.1 The audit

Removing the LunaP constraint does not by itself make MIT available — it only removes the one term everybody already knew about. Everything else linked here had to be checked, read out of each package's own `<license>` expression in its `.nuspec` rather than from memory:

| Licence | Packages |
|---|---|
| MIT | `FSharp.Core`, `YDotNet`, `YDotNet.Native.Linux`, `YDotNet.Native.MacOS`, `YDotNet.Native.Win32`, `Microsoft.Data.Sqlite`, `Microsoft.NET.Test.Sdk`, `Avalonia.Headless`, `EmuSen.LunaP` |
| Apache-2.0 | `SQLitePCLRaw.bundle_e_sqlite3`, `SQLitePCLRaw.core`, `xunit`, `xunit.runner.visualstudio` |
| BSD-3-Clause | `FsCheck`, `FsCheck.Xunit` |

**No copyleft anywhere in the tree**, and the four Apache-2.0 and two BSD-3-Clause entries are all test-only except the SQLitePCLRaw pair, which are permissive and impose notice terms rather than reciprocal ones. Ownership is equally simple: 26 commits, one author.

The `FSharp.Core` line is worth pausing on, because §7 records it as the objection that sank EmuSen's `F#ascent` branch and settles it structurally by moving to a separate repository. It is MIT, and was never a licence problem — it was an assembly-boundary problem. Two different objections that would have been easy to conflate.

### 14.2 What stays GPL

`v0.1.0` was released as compiled binaries under GPL-3.0-or-later, and `EmuSen.Pegasus.Core` `0.2.0`, `0.2.1` and `0.2.2` are on nuget.org under it. **All of them stay.** nuget.org cannot edit a published package's metadata — a version may be unlisted but not altered — and a grant already made to somebody who took the work is not withdrawn by a later, looser one. Source for the `v0.1.0` binaries remains this repository at that tag.

A relicence is not a recall, and nothing is being unlisted. The README's older correction about the `LICENSE` file having been missing until that first binary release is kept for the same reason: it is a record of the repository having declared a licence in prose without shipping the text of one, and MIT does not make that less worth remembering.

`EmuSen.Pegasus.Core` goes to **0.3.0** to carry the change. The protocol is untouched at 4 — a 0.2.x peer and a 0.3.0 peer interoperate exactly as before — and the bump exists only because a licence is the one thing a consumer most needs a version number to signal and there is no other mechanism for signalling it. That is the same reasoning LunaP applied at its own 0.6.0.

### 14.3 A hazard the table does not reach

`YDotNet.Native.Linux` ships `runtimes/linux-*/native/libyrs.so` — the Yrs CRDT compiled from Rust — and the macOS and Win32 packages ship the equivalent. All three declare MIT in their nuspecs and **none of them ships a licence file of any kind**. The packages point at <https://github.com/y-crdt/ydotnet> at a pinned commit, so the provenance is traceable, but the compiled artefact travels into every Pegasus binary with no notice beside it.

This is recorded as a hazard rather than a defect, and it is not ours to fix: it is the packagers' declaration to make, and it was equally true while this repository was GPL. It is noted because it is precisely the question a reader auditing an MIT claim should ask — *what is actually inside the binaries* — and because LunaP's §25.4 records the same shape of finding about the Inter typeface embedded in `Avalonia.Fonts.Inter`. Two dependencies, both declaring a permissive licence over a compiled third-party artefact they ship without its notice. Worth knowing that the pattern is common rather than a one-off.

## 15. The release became a workflow

`v0.1.0` was built by hand: four `dotnet publish` runs on one Linux laptop, four staging directories assembled beside them, `sha256sum` over the result, and `gh release create` typed at the end. It worked, and it left nothing behind. **No script in this repository produced those binaries and no file records the flags they were built with.** What the archives prove is that somebody ran a command once. §14 then relicensed the work and made a second release necessary, which is what turned a tolerable gap into the thing that had to be fixed first — a release cut by hand a second time would have been a second unrepeatable event, and the difference between the two would have been unauditable in exactly the way a licence change must not be.

`.github/workflows/release.yml` takes the `v*` tag namespace §7.1 reserved for the application before there was an application release to put in it. `core-v0.3.0` does not match `v*` — it does not start with a `v` — so the two publishing workflows in this repository cannot fire on each other's tags.

### 15.1 What building on the target OS buys, and what it does not

This is the part worth stating precisely, because it is easy to overclaim and the release notes are what a reader trusts.

v0.1.0's macOS and Windows binaries were **cross-compiled on Linux and had never been run**, and both the release notes and `Pegasus_Setup.md` §11 said so. The matrix changes the first half of that sentence and not the second. Each RID is now built by the operating system it targets — `macos-13` for `osx-x64`, `macos-14` for `osx-arm64`, `windows-latest` for `win-x64` — so "built on a Mac" is now true where only "built for a Mac from Linux" was available before.

**Nobody has still started them.** Pegasus is a GUI, a runner has no display, and there is no `--help` path to exercise instead; the program's only entry point opens a window. So the caveat stays on the page, narrowed rather than removed, and any release note claiming these were tested would be inventing a result. Chariot's identical workflow in its own repository *can* smoke-test, because a daemon has a `--help` that parses arguments and exits — that asymmetry is real and is recorded there rather than borrowed here.

The runner images are pinned by number rather than `macos-latest` for a reason with a history: `latest` already moved from Intel to Apple Silicon once. The day it moves again is the day `osx-x64` silently goes back to being cross-compiled with nobody editing this file, which is the exact failure this matrix exists to prevent.

`fail-fast: true` is deliberate. A release missing one platform is worse than no release, because `SHA256SUMS` would be internally consistent over what it lists and silent about what never built.

### 15.2 The version comes from the tag

The same discipline `publish-core.yml` states for the package, for the same reason: a version written in two places will eventually disagree with itself. The tag's `v` is stripped and passed as `-p:Version`, so the tag decides what the binaries report and what the archives are called. The `<Version>` in `EmuSen.Pegasus.fsproj` is the default for a local `dotnet publish` and is kept in step.

The archive layout is v0.1.0's, unchanged on purpose: a single top-level `Pegasus-<rid>` directory so an extract never scatters files into the caller's, and `LICENSE` and `README.md` beside the binary in every archive. `Pegasus_Setup.md` §2.1 documents those paths and a reader following it must not find something else. The macOS `.app` bundle is constructed in the workflow — `Contents/MacOS`, `Contents/Info.plist`, bundle id `io.github.redque3n.pegasus`, and **no `CFBundleIconFile`**, because nothing in this repository has ever had an icon and a key pointing at a file that is not there is worse than an absent key.

Two measurements, both taken rather than remembered. A local `linux-x64` self-contained single-file publish at 0.2.0 is **105,900,038 bytes**; v0.1.0's `linux-x64` download was **44,916,243 bytes** compressed, which is where the release notes' "a 100 MB program arrives as a 45 MB download" comes from. The suite is **160 tests, all passing**, and it gates the matrix rather than running inside it — it is headless and platform-independent, so four runs would buy nothing and would turn one red test into four.

### 15.3 What is still not covered

- **Nothing is signed or notarised.** Signing needs a paid certificate from Apple or a code-signing CA and there is not one, so every platform warns once and `SHA256SUMS` is the only integrity evidence a download has. That file is now generated on the runner over the artefacts about to be uploaded, rather than on a laptop over files about to be uploaded from somewhere else — a smaller gap than it was, and not a closed one.
- **The GUI is never launched by the release**, per §15.1.
- **No `linux-arm64` build**, so a Raspberry Pi still cannot run a released Chariot or Pegasus. Building from source on the machine is one `dotnet publish` and `Pegasus_Setup.md` §11 says so.
- **The release notes are prose in the repository** at `docs/Releases/<tag>.md`, so they are reviewed with the change they describe. A tag with no notes file falls back to generated notes rather than failing: the binaries are the thing being shipped and a missing paragraph must not block them.

### 15.4 0.2.0 was spent on a retired runner image, and the pin is what spent it

**`v0.2.0` was tagged, built three platforms, published nothing, and did not fail.** It is recorded here rather than quietly retagged because the failure mode is the interesting part and somebody will meet it again.

**The tag itself no longer exists** — it was deleted once 0.3.0 had shipped, so the release list does not carry a version that published nothing. That makes this section the only remaining record of it, which is the reason the run is described here in enough detail to be useful without it: nothing was ever distributed under `v0.2.0`, so deleting it took nothing back from anybody.

The matrix above asked for `macos-13` for the `osx-x64` build. **That image was retired on 4 December 2025.** The observed run — `test` green, `linux-x64` green, `win-x64` green, `osx-arm64` green, `osx-x64` **queued**, indefinitely — is what a retired label looks like from the outside. The replacement is `macos-15-intel`, and the matrix now names it.

Three things in that are worth keeping.

**A retired runner label does not error, it queues.** There is no red job, no message, and nothing in the run's summary that says the label is gone. `fail-fast: true` never tripped because nothing failed, and §15.2's own reasoning — that a release missing a platform is worse than no release — worked exactly as designed: `release` needs all four builds, three arrived, and the job simply never started. **The safety property held and the diagnosis was still invisible.** A release that fails loudly is a better outcome than three greens and silence, and this file has no way to ask for that; the check has to be a human reading the run, or a date in a calendar.

**The pin is what caused it, and unpinning is still the wrong fix.** §15.1 argued for naming images explicitly rather than `macos-latest`, on the grounds that `latest` had already moved from Intel to Apple Silicon once and would silently turn `osx-x64` back into a cross-compile. That argument is unchanged and still right. But a pin trades drift for expiry: `macos-latest` would have kept building something, whereas `macos-13` stopped building anything. **Both failure modes are real and only one of them is quiet in the direction that matters** — a cross-compiled binary ships and is wrong about how it was made; a retired image ships nothing at all. The second is the safer of the two to have, and it is the one to plan for rather than design away.

**There is now a date this repository has to meet.** `macos-15-intel` is the **last x86_64 image GitHub Actions will offer, and it goes away in August 2027.** Apple has discontinued the architecture and Actions follows it. After that, `osx-x64` cannot be built on Intel hardware here at all, and the choice will be to cross-compile it from an Apple Silicon runner — which is a real option and would need this section's honesty about what "built on a Mac" then means — or to stop shipping it. It is written down because a runner image with a known end date is exactly the kind of thing that is remembered right up until the release it breaks.

Chariot took the identical defect from the identical file on the same day, and `Chariot_Design.md` §12.3 records it there.
