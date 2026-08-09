# Pegasus sync: pairing, frames, and what the encryption is for

## 1. Topology

One peer hosts and the other joins. This describes who opens the socket and
nothing else — it is not a client/server split, and the host is not authoritative.
Both sides hold a complete replica and both persist it (`Pegasus_Format.md`), so
the host disappearing costs the joiner nothing but liveness.

An always-on relay is deferred, not rejected. The session abstraction is shaped so
that a relay is simply a peer that never disconnects; adding one should not
require the protocol to learn a new role.

## 2. Pairing

The host displays an address and a join code such as `7-lantern-quartz`. The
joiner types both. The code is drawn from a 32-word list with a leading digit,
giving `9 x 32 x 32` ≈ 9,216 combinations.

That is small, and deliberately so — it is a short-lived code read aloud across a
room or over a phone, and §5 explains what it is actually protecting against. The
words were chosen to be unambiguous when spoken.

The code is case- and whitespace-insensitive, because it will be retyped by hand.

## 3. Frame format

A frame is a tag byte followed by a payload:

    0  Hello       peer id, display name, colour  (length-prefixed UTF-8 strings)
    1  SyncStep1   raw Yjs state vector
    2  SyncStep2   raw Yjs update answering a state vector
    3  Update      raw Yjs update
    4  Awareness   peer, then caret and anchor as int32
    5  Bye         no payload

Strings use `BinaryWriter`'s 7-bit-encoded length prefix. Multi-byte integers are
little endian.

The encoding is bespoke rather than JSON because `System.Text.Json` cannot
serialise F# unions and `PeerId` is one; the full account is in
`Pegasus_Design.md` §4.6. The payloads of tags 1–3 are ordinary Yjs bytes, which
keeps a future bridge to a `y-websocket` client a translation at the frame
boundary rather than a change to the document model.

## 4. Exchange

On connect, each side sends `Hello`, then `SyncStep1` carrying its state vector.
On receiving `SyncStep1`, a peer replies with `SyncStep2` containing exactly the
operations the other lacks. Thereafter each local edit is broadcast as `Update`.

Because the payloads are Yjs updates, the exchange is idempotent and
order-independent: a duplicated or late `Update` merges to the same document. The
protocol needs no acknowledgements and no sequence numbers, and a reconnect is
just another `SyncStep1`.

`Awareness` is sent on caret movement and is not persisted. Presence is
disposable by nature; a stale cursor is noise, not data.

## 5. What the encryption is and is not

Every frame is sealed with AES-256-GCM under a key derived from the join code by
PBKDF2-HMAC-SHA256, 210,000 iterations, with a fixed salt. Each frame carries a
fresh random 12-byte nonce, so no counter has to survive a reconnect. The
handshake is an HMAC challenge/response that proves both sides derived the same
key without putting it on the wire.

**The salt is fixed, and that is a real weakness.** Both peers must derive the
same key from the code alone, with no round trip to agree on a random salt. A
fixed salt means an attacker can precompute against the 9,216-code space. The
iteration count raises the cost of doing so but does not change the shape of the
problem.

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
an ordinary Pegasus document holding a map of note id to display name.

This is the design's one piece of genuine economy. Note creation and rename are
concurrent operations on shared state and therefore need conflict resolution —
and because the index is just another document, they get the same CRDT, the same
file format, the same crash recovery and the same sync path as note text. There
is no second mechanism to write, test, or reason about.

Deletion tombstones the index entry and leaves the file on disk. Nothing the user
authored is removed by the application.
