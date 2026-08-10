# EmuSen.Pegasus.Core

The vocabulary two Pegasus programs have to agree on: the wire types, the frame
encoding, the sealed envelope, and identity keys.

This is not a general-purpose library. It exists because
[Pegasus](https://github.com/RedQuE3n/EmuSen.Pegasus) — a CRDT-backed shared
notepad — and `EmuSen.Chariot` — the relay it reaches peers through — have to
speak the same protocol, and the alternative was two implementations of a frame
format kept in step by hand. It is published so the two can be developed in
separate repositories, not because it is expected to be useful on its own.

It declares **one dependency, `FSharp.Core`**, and a test keeps it that way.
Nothing here merges a document and nothing here opens a window: a relay is a
router and a mailbox, so a server taking this package takes no CRDT and no UI
toolkit with it.

## What is in it

| | |
|---|---|
| `Types` | `Handle`, `PeerId`, `PeerInfo`, the `Frame` union, the routing `Envelope` |
| `Codec` | frames and envelopes to bytes and back |
| `Crypto` | AES-256-GCM sealing, PBKDF2 key derivation, join codes |
| `Identity` | ECDSA P-256 keypairs, fingerprints, signing and verification |
| `Attestation` | the mutual identity proof — a challenge each way, a signature each way |
| `Agreement` | authenticated ephemeral key agreement for a control channel |
| `Framing` | the length-prefixed wire, readable two ways: whole frames if you hold the key, envelope-and-opaque-payload if you do not |
| `Handshake` | the pre-shared-key challenge/response that gates a connection |

The split in `Framing` is the design's hinge. A peer holds the join code and
reads whole frames; a relay holds no join code, reads only the destination, and
moves a sealed payload it could not open if it wanted to.

## What the sealing is and is not

Honestly stated, because a package that ships crypto and is vague about it is
worse than one that ships none. Frames are sealed with AES-256-GCM under a key
derived from a short spoken join code by PBKDF2-HMAC-SHA256, **with a fixed
salt** — both peers must reach the same key from the code alone, with no round
trip in which to agree a random one. An attacker can precompute against the
code space; the iteration count raises the cost without changing the shape of
the problem.

This is a pre-shared key for two people on a private network. It is not
protection against someone who watched the pairing, and there is no forward
secrecy on that path. The full threat model is `docs/Pegasus_Sync.md` §5 in the
Pegasus repository.

## Licence

GPL-3.0-or-later.
