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

Tag 8 is Chariot's, not a peer's: two people on a socket already know who is
there. `Chariot_Design.md` §6 covers what a roster is for. The count is checked
against the frame it arrived in before anything is allocated, because a count is
a claim until it has been looked at.

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

Two peers on a socket always send `Direct`, and a session refuses anything else,
because nothing put a plain connection behind a relay. When Chariot delivers, it
rewrites the destination to `Direct`: the recipient *is* the destination, so
there is nothing left to route.

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

A second one, in the same category: a `Session` is given the document that was
open when it was created and holds it for its lifetime. Opening a different note
while connected disposes that document underneath the live session. Disconnect
first. This is untested — there is no case in the suite that switches notes on a
connected session, and the failure mode is therefore stated as a hazard rather
than as an observed behaviour.

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
