# Pegasus sync: pairing, frames, and what the encryption is for

## 1. Topology

One peer hosts and the other joins. This describes who opens the socket and
nothing else — it is not a client/server split, and the host is not authoritative.
Both sides hold a complete replica and both persist it (`Pegasus_Format.md`), so
the host disappearing costs the joiner nothing but liveness.

An always-on relay is deferred, not rejected.

**A correction.** This section used to say the session abstraction was shaped so
that "a relay is simply a peer that never disconnects", and that adding one
"should not require the protocol to learn a new role". That was wrong, and the
reason is worth keeping because it is not obvious.

Merging a Yjs update requires the plaintext of that update. So a relay that
holds a replica — a peer — must be able to decrypt everything both parties
write, and the end-to-end property would be gone. Keeping the sealing means the
relay cannot merge, which means it is **not a peer**: it is a router and a
mailbox. `EmuSen.Chariot` is built on that footing, and `Chariot_Design.md` §4
is the long version.

The protocol did have to learn something new, and §3 now carries it: a
destination outside the seal, because a frame sealed end to end leaves an
intermediary nothing it can read. That is one envelope byte on a direct
connection and the prediction was still wrong.

The host accepts exactly one joiner. `Host.AcceptAsync` is awaited once, so a
session is a pair and not a group. This is a property of the current shell rather
than of the protocol — the frame layout has nothing in it that assumes two — but
it is what ships, and a reader should not infer more from "peer" than that.

## 2. Pairing

The host displays an address and a join code such as `7-lantern-quartz`. The
joiner types both. The code is drawn from a 32-word list with a leading digit,
giving `9 x 32 x 32` ≈ 9,216 combinations.

That is small, and deliberately so — it is a short-lived code read aloud across a
room or over a phone, and §5 explains what it is actually protecting against. The
words were chosen to be unambiguous when spoken.

The code is case- and whitespace-insensitive, because it will be retyped by hand.

Signing in is not part of pairing and does not replace the code. A handle
identifies you to the other peer; the join code is still the only thing
gating the connection. `Pegasus_Identity.md` §2 explains why a password could
not have taken that job without a server to check it against.

The listening port is chosen by the operating system, not by the user, and is
displayed alongside the code. It therefore differs every session, which is a
nuisance for anyone trying to forward a fixed port and is a further reason §5
recommends a tunnel instead.

**This is one of two ways to pair, and the other one is now in the window.** With
a relay there is no address and no port: both people sign in to a server, pick
each other out of a buddy list, and type the code they agreed. The code does not
go away — §4.1 is emphatic about why — but the two things that changed every
session do. What is above stays the way two people on one LAN pair with nothing
in the middle.

## 3. Frame format

A frame is a tag byte followed by a payload:

    0  Hello       protocol byte, peer id, handle, colour, then the
                   sender's public key, int32-length-prefixed
    1  SyncStep1   raw Yjs state vector
    2  SyncStep2   raw Yjs update answering a state vector
    3  Update      raw Yjs update
    4  Awareness   peer, then caret and anchor as int32
    5  Bye         no payload
    6  Challenge   a random nonce
    7  Proof       a signature over the other side's nonce
    8  Roster      int32 count, then that many peers
    9  Agree       int32-length-prefixed ephemeral public key, then a
                   signature over it

Tags 8 and 9 are Chariot's, not a peer's. Two people on a socket already know who
is there, and they already seal under a join code no intermediary has, so they
have nothing to agree — a peer that sends either is refused. `Chariot_Design.md`
§6 covers what a roster is for and §4.3 below covers what an agreement is for.
The roster count is checked against the frame it arrived in before anything is
allocated, because a count is a claim until it has been looked at; `Agree` states
the boundary between its two blobs for the same reason `Hello` does.

Strings use `BinaryWriter`'s 7-bit-encoded length prefix. Multi-byte integers are
little endian.

Tags 6 and 7 are the identity exchange; `Pegasus_Identity.md` §2 covers what a
proof does and does not establish. `Hello` carries the protocol version so a
build mismatch is stated in the first frame rather than discovered as a decode
failure further down.

### 3.1 The envelope

A whole frame is sealed, which leaves an intermediary nothing to read, and a
relay has to know where a payload is going. So the wire is:

    int32   length of everything that follows
    bytes   envelope, IN THE CLEAR
    bytes   sealed payload

with two envelopes:

    0  Direct           no intermediary; nothing to say
    1  ToHandle         a destination handle, 7-bit-length-prefixed UTF-8
    2  FromHandle       who a relay says a delivery came from

`FromHandle` is stamped by a relay on delivery, because an `Update` is opaque
bytes and says nothing about who wrote it. It is the relay's word and not proof:
the relay knows because that connection signed in, so it is exactly as
trustworthy as the relay, which is why what it names is a handle to route by and
never a reason to skip a signature. A client that sends one is claiming to be
the server, and Chariot refuses it.

Two peers on a socket always send `Direct`, and a session refuses anything else,
because nothing put a plain connection behind a relay. When Chariot delivers it
rewrites the destination to `FromHandle`, naming the sender: the recipient *is*
the destination, so what is left to say is not where it is going but where it
came from.

**This leaks metadata and there is no way around it.** A relay necessarily
learns who is connected, who sends to whom, when, and how many bytes. It does
not learn content, and a test pins exactly that: a reader holding no key gets
the destination and a payload it cannot open with a wrong code or a zero key.
Anyone who needs the routing itself hidden wants an onion router, not this.

A channel identifier derived from the join code was considered instead of a
handle, so a relay would route without learning who talks to whom. It is not
obviously worth it — presence already requires Chariot to know handles and
connections, so the handle is known anyway — and it is recorded in
`Chariot_Design.md` §5 so it is not re-proposed without a better argument.

The peer id is a fingerprint of the sender's public key and the handle is the
name they signed in under, both described in `Pegasus_Identity.md` §6 and §1.
An earlier version of this paragraph said neither was proven, and that is no
longer true: §4 now carries a challenge and a proof, and the id must be the
fingerprint of the key that arrives beside it.

On the wire each frame is sealed (§5), preceded by its envelope (§3.1), and the
pair length-prefixed with an int32.

The encoding is bespoke rather than JSON because `System.Text.Json` cannot
serialise F# unions and `PeerId` is one; the full account is in
`Pegasus_Design.md` §4.6. The payloads of tags 1–3 are ordinary Yjs bytes, which
keeps a future bridge to a `y-websocket` client a translation at the frame
boundary rather than a change to the document model.

## 4. Exchange

On connect, each side sends `Hello` — carrying its public key — and a
`Challenge` holding a fresh random nonce. Each signs the nonce the other sent
and replies with `Proof`.

**Only after a proof verifies** does a peer send `SyncStep1` carrying its state
vector, subscribe to local edits, or accept anything that touches the document.
On receiving `SyncStep1`, a peer replies with `SyncStep2` containing exactly the
operations the other lacks. Thereafter each local edit is broadcast as `Update`.

The ordering is the point rather than an implementation detail: a peer that
fails the proof must learn nothing and change nothing, so nothing about the
document may be sent or applied before it passes. Document traffic arriving
early is refused, and a test drives exactly that with a client that skips the
exchange.

Because the payloads are Yjs updates, the exchange is idempotent and
order-independent: a duplicated or late `Update` merges to the same document. The
protocol needs no acknowledgements and no sequence numbers, and a reconnect is
just another `SyncStep1`.

`Awareness` is sent on caret movement and is not persisted. Presence is
disposable by nature; a stale cursor is noise, not data.

A limitation of the current shell rather than of the protocol: `Awareness` frames
are produced and delivered, and the session raises them, but nothing in the window
subscribes, so remote carets are not drawn. The transport half is complete and
tested; the presentation half is not written.

**A correction, and a hazard that is now a behaviour.** This section used to say
that a `Session` is given the document that was open when it was created and
holds it for its lifetime, so opening a different note while connected disposes
that document underneath the live session — and that the reader should disconnect
first. That was accurate and it was stated as a hazard rather than a behaviour
because nothing in the suite drove it.

It is no longer left to the reader. Opening a note now drops whatever is
connected first, and a test in the UI suite drives exactly that: two peers
converge over a socket, one switches notes, and the assertion is that the
connection went to `Offline` and the note switched to is still a working
document. Removing the disconnect turns it red.

What changed was not the hazard but how likely it is. A direct session is opened
for a note and closed when you are done with it; a conversation through a relay
outlives a moment of interest in one note, because the buddy list is the thing
you leave open. Disposing a native Yjs handle another thread is still using is
not a failure anybody could act on. Dropping a connection is visible and
recoverable, so that is what happens.

## 4.1 Through a relay

The same exchange, wrapped. A peer's frames are sealed under the join code
exactly as before and addressed `ToHandle` instead of `Direct`; deliveries come
back stamped `FromHandle`. Nothing about the conversation changes, which is why
`Conversation` was split out of `Session` — the protocol is identical and only
the transport differs, and two implementations of an identity handshake is how
one of them ends up weaker than the other.

**One socket, two key domains**, and this is the part to hold on to. Frames
addressed to the relay — signing in, the roster — are sealed under a key derived
from the server passphrase. Frames for a peer are sealed under the join code,
which the relay does not have and cannot derive. The envelope is what keeps them
apart.

So what a relay removes is the address and the port, and **not** the join code.
The code is the key your notes are sealed under; it still has to be agreed out
of band. A test asserts the property that matters by inspecting everything a
relay actually carried: none of it is readable with the server's own key.

Either end may open a conversation, and neither has to speak first. A client
that has said what note it is willing to be invited into will create a
conversation on first contact from a stranger, because both ends open at once
and an ordering rule cannot win that race.

### 4.2 The half handshake, and the re-greeting that fixes it

Found by building the window rather than by reading the protocol, which is the
usual way.

Two people click **Open note** a few seconds apart, because that is what people
do. Whoever clicks first sends a `Hello` and a `Challenge` into a client that has
no conversation to receive them with, and both are dropped. When the second one
opens, its `Hello` and `Challenge` arrive at a client that *is* listening — so the
late end learns who the early end is, proves itself, and sends `SyncStep1`. The
early end has never received a proof over *its* nonce, so it is not proven, and
it refuses that `SyncStep1` as document traffic from an unproven peer. Half a
handshake, and from the outside it looks like the relay eating frames.

The fix: **the first `Hello` a conversation receives is answered by sending our
own opening move again** — the same `Hello`, and the same `Challenge` carrying
the same nonce, so it is one challenge repeated and not a second one. A `Hello`
is the first evidence that anybody is listening, so it is the moment to say it
again.

Once, and the bound matters. An unconditional answer would have two peers
greeting each other for the rest of the afternoon; one flag makes the exchange
terminate after each side has re-greeted at most once. On a direct socket, where
nothing is ever dropped, the cost is two extra frames each at setup and a
duplicate proof, which is why `Proof` is now ignored when the conversation is
already proven. That guard is load-bearing rather than tidiness: re-running the
branch would subscribe to local edits a second time and send every keystroke
twice — harmless to a CRDT, which is exactly why it would never have been
noticed.

A test drives the case directly: two clients, neither willing to be invited, one
opening 300ms before the other. Removing the re-greeting turns it red and leaves
the other four relay tests green, which is what makes it a test of this and not
of the relay in general.

### 4.3 Signing in to a relay, and why the passphrase stopped being a key

Two things were wrong with the previous exchange, and they are the same thing
seen from opposite ends.

**Chariot did not prove itself.** It answered a client's `Challenge` with an HMAC
over the passphrase key — a proof that it held the passphrase, which every client
holds, and therefore a proof of nothing about *who* it was. A client's only
assurance it had reached the right server was that the server knew a secret
shared with everybody.

**The passphrase read everything.** Control traffic — sign-in, rosters — was
sealed under a key derived from that same passphrase, with the fixed salt §5
already calls a weakness. So any client could read any other client's roster off
the wire, and a recorded session stayed readable to whoever later learned one
shared secret. The passphrase was meant to be a doorbell and had quietly become a
key.

The exchange now runs:

    door    Handshake over the passphrase key, unchanged. The doorbell.
    →       Hello   the server's peer info and public key
    →       Challenge  the server's nonce
    ←       Hello   the client's peer info and public key
    ←       Challenge  the client's nonce
    ←       Proof   the client signs the server's nonce
    →       Proof   the server signs the client's nonce
    →       Agree   the server's ephemeral, signed
    ←       Agree   the client's ephemeral, signed
    ...     everything after this is sealed under the agreed key

Both `Agree` frames go under the door key, because the session key is the thing
they produce, and both ends switch immediately after — the server on receiving
the client's, the client on sending it. There is no frame in between, so there is
no window in which one end is talking in a key the other is not reading.

Four properties are worth stating precisely, because a key agreement is easy to
describe as stronger than it is:

- **The client pins the server's key**, in the same table and by the same rule it
  pins a person's: trust on first use, refuse a change — `Pegasus_Identity.md`
  §7. A server and a person therefore share one namespace of handles per owner,
  which is deliberate. One name, one key.
- **The signature on an ephemeral is not decoration.** An unsigned ephemeral can
  be replaced by whoever carries it, and both ends would agree a key with the
  attacker instead of with each other. Each side signs its own ephemeral, under
  its own domain tag, over the nonce the *other* side challenged it with — so a
  signed ephemeral recorded from one session cannot be replayed into another.
- **Nothing derives a key before the far side has proved itself.** An ephemeral
  signed by an unproven identity is an ephemeral signed by anybody, which would
  be unauthenticated Diffie-Hellman and no better than the passphrase it
  replaces.
- **Note traffic is untouched**, and must stay so. It is sealed under a join code
  Chariot never has, which is the property the whole design rests on; wrapping it
  in a second key agreed *with* the server would be strictly worse.

The KDF is `ECDiffieHellman.DeriveKeyFromHash` on P-256 — SHA-256 over
`salt ‖ Z ‖ tag`, the concatenation KDF of SP 800-56A — producing exactly the 32
bytes AES-256-GCM wants with nothing imported that was not already here. The salt
is both nonces, server's first, so two connections between the same pair never
agree the same key. P-256 rather than X25519 for the same reason `Identity` uses
it: `Pegasus_Identity.md` §5.

**What this does and does not buy.** Control traffic now has forward secrecy: the
ephemeral private halves are never stored and are gone when the connection ends,
so a recording is not readable later even by someone who learns the passphrase.
It does *not* make the passphrase strong — it is still a fixed-salt PBKDF2 over a
shared secret, and it is still what decides who may open a connection at all. It
buys the passphrase back its intended job.

## 5. What the encryption is and is not

Every frame is sealed with AES-256-GCM under a key derived from the join code by
PBKDF2-HMAC-SHA256, 210,000 iterations, with a fixed salt. Each frame carries a
fresh random 12-byte nonce, so no counter has to survive a reconnect. The
handshake is an HMAC challenge/response that proves both sides derived the same
key without putting it on the wire.

**The salt is fixed, and that is a real weakness.** Both peers must derive the
same key from the code alone, with no round trip to agree on a random salt. A
fixed salt means an attacker can precompute against the 9,216-code space (§2).
The iteration count raises the cost of doing so but does not change the shape of
the problem.

So the honest statement of what this buys:

- A machine that stumbles onto the listening port cannot read the notes or inject
  edits without the code.
- Frames cannot be tampered with undetected in transit, because GCM authenticates
  them.

And what it does not buy:

- It is not protection against someone who can watch the pairing happen.
- It is not protection against an adversary willing to spend real compute on a
  9,216-entry keyspace.
- There is no forward secrecy. A recorded session is readable by anyone who later
  learns the code.

This is a pre-shared key for a notepad two people run across a LAN or a private
network. Anyone wanting the stronger property should tunnel Pegasus over
something that provides it — WireGuard or Tailscale — rather than trusting this
layer to be more than it is. Frames are capped at 64 MiB so a hostile length
cannot drive an unbounded allocation before authentication has a chance to fail.

## 6. The workspace index

A workspace is a directory of `.pegasus` notes plus `_index.pegasus`. The index is
an ordinary Pegasus document.

This is the design's one piece of genuine economy. Note creation and rename are
concurrent operations on shared state and therefore need conflict resolution —
and because the index is just another document, they get the same CRDT, the same
file format, the same crash recovery and the same sync path as note text. There
is no second mechanism to write, test, or reason about.

The index holds one line per entry, `id \t name \t deleted`, appended rather than
edited in place; the last line mentioning an id wins. A rename is therefore an
append, not a mutation. Keeping it a plain `Y.Text` avoids introducing a second
root type for what is already a solved problem — an earlier draft of this section
described it as a map, which the implementation never was.

Deletion tombstones the index entry and leaves the file on disk. Nothing the user
authored is removed by the application.

## 7. Messages, and why protocol 5 is a hard break

Protocol 5 adds a second kind of traffic. Notes are unchanged, frame for frame,
and everything §1 to §6 says about them still holds.

**This is not a tag-only bump and an older build cannot be talked round.** Every
previous protocol change added frames, and a frame tag an old build does not know
is a frame it can refuse cleanly. This one changed the **envelope** — a routed
frame now names which channel it is on, and a delivery names the mailbox row it
came out of — and the envelope is the one part of a frame a relay reads while
holding no key at all. A protocol-4 relay handed a protocol-5 envelope reads the
handle it expects and then finds bytes it has no field for. `Hello` says 5 in the
first frame, which is what turns that into a refusal instead of a decode failure
three frames later.

The frames added: `Card`, `Ask`, `Unknown`, `Message`, `Ack`, `Undeliverable`.
Of these only `Message` is ever sealed end to end; the rest are control traffic
between a client and its relay and travel under the agreed session key of §4.3.

### 7.1 A message needs no join code

A note is sealed under a key derived from a code both people typed. That is a
pre-shared key, §5 is honest about what it is and is not, and **it cannot work
for a message**, for a reason that has nothing to do with its strength: a message
has to be sealed for somebody who is *asleep*. There is nobody at the far end to
agree anything with, and asking two people to agree a password before they can
say hello is not an instant messenger — it is a shared notepad with a chat box in
it.

So the recipient's key has to be knowable in advance, which means published,
which is what a `Card` is: an identity's messaging public key, signed by its
identity key, served by the relay to whoever asks (`Chariot_Design.md` §14).

The user-visible consequence is that one window now contains two different
pairing stories, and the buddy panel says so in words rather than leaving it to
be inferred: **a message needs no code; a shared note still does.** Somebody who
learned the join code rule from §2 and then found they could message a buddy
without one would reasonably conclude the code had stopped mattering. It has not.

### 7.2 Two agreements, and what each one buys

A message body is sealed with AES-256-GCM under a key derived from **two**
Diffie-Hellman agreements hashed together:

    H1 = ECDH(ephemeral_sender, messaging_recipient)
    H2 = ECDH(messaging_sender,  messaging_recipient)
    K  = SHA-256(domain || salt || H1 || H2)

with the ephemeral public key sent in the clear beside the body, and the salt
carrying that ephemeral key and both parties' messaging keys so a ciphertext is
bound to the exact pair it was written for.

Each half buys one property and removing either takes it away:

- **H1 is why a recording does not stay readable.** The ephemeral private half is
  generated per message and stored nowhere, so somebody who later steals the
  *sender's* disk cannot recompute K for anything already sent.
- **H2 is why the sender is the sender.** Only a holder of the sender's messaging
  private key can compute it. Without it, anybody who knows the recipient's
  published key — which is everybody, that is what published means — could seal a
  message and let the relay's `FromHandle` stamp name whoever they liked. That
  stamp is the relay's word and never proof (`Types.fs`); H2 is the proof.

**A signature over the body was the obvious alternative and was rejected.** It
would have authenticated the sender more simply, and it would have made every
message a transferable proof that a named person said a specific thing — which a
private conversation should not manufacture as a side effect. H2 authenticates to
the *recipient* and to nobody else, because the recipient could have computed the
same key itself and therefore could have written the message. That is the same
deniability property Signal's design goes to trouble to keep, obtained here for
one extra ECDH.

This is X3DH with the parts that need a server-held pool of one-time prekeys left
out.

### 7.3 What this does not buy

Stated in the same shape as §5, because "encrypted so only the recipient can read
it" is the kind of sentence that grows in the retelling:

- **No post-compromise security.** The recipient's messaging key never rotates on
  its own, so stealing it opens every message ever sealed to it, past and future.
  A ratchet is what fixes that and there is no ratchet here.
- **Forward secrecy is one-sided.** H1 protects against the sender being
  compromised later. It does nothing about the recipient's key, which is the
  other half of every agreement.
- **No protection against a relay that lied about the first card.** Trust on
  first use has a first use (`Pegasus_Identity.md` §7).
- **The metadata is worse than the notes', not better.** Chariot learns who
  messages whom, when, how much, and now also *that it was a message rather than
  a note edit*. It does not learn content. Anyone who needs the routing itself
  hidden wants an onion router and should be told so.

What it does buy, plainly: a relay operator holding the database and the
passphrase, or an attacker who has taken both, has ciphertext and a social graph.
Nothing in a queued message opens for them.
