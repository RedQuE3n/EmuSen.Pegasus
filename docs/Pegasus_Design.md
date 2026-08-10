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
  require checking out an emulator, which gives up most of what the split was for.
- **Drop LunaP and use raw Avalonia.** Rejected on evidence rather than taste —
  §8 records that the hand-rolled bootstrap this would return to is exactly what
  silently dropped `UseX11` on Wayland.
- **Publish LunaP as a NuGet package.** Adopted.

`EmuSen.LunaP` is packed at 0.1.0 together with the two dependency-free leaves it
names, `EmuSen.Galaxia` and `EmuSen.Cauldron`, because a consumer outside that
repository cannot resolve a `ProjectReference`.

This does not weaken LunaP's layering rule; it enforces it. That rule says LunaP
may reference Avalonia, Galaxia and Cauldron and nothing else, and its purpose is
to stop a launcher acquiring an entire emulator by accident. A package cannot
reach back up into a core at all, so the constraint that was a comment in a
`.csproj` is now a property of the artifact.

Two limitations, stated rather than discovered later:

- The package feed is currently a folder, `local-packages/`, that `NuGet.config`
  resolves relative to itself. It is populated by `dotnet pack` in EmuSen-Project.
  GitHub Packages is the intended destination and the reason this is a folder
  today is only that the packages have not been pushed there yet.
- `EmuSen.Galaxia` ships its catalogue schema as `.sql` files copied to the build
  output. Those do **not** travel in the package, because they are `None` items
  rather than packaged content. Pegasus does not use the catalogue, so this costs
  nothing here; anyone packaging Galaxia for a consumer that *does* want the
  catalogue has to fix it first.

A licence consequence follows and is worth naming: EmuSen is GPL-3.0, so the
packages are GPL-3.0, so Pegasus is a derivative work of them. This repository's
licence is therefore not a free choice while §8 holds.

`EmuSen.Pegasus.Core` now leaves by the same road it arrived on. Chariot is in
another repository and cannot resolve a `ProjectReference` either, so the core
is packable and Chariot consumes it from `local-packages/` until it is on GitHub
Packages — the identical arrangement, and identically temporary.

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
