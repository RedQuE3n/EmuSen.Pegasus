# Pegasus identity: handles, keys, and what a sign-in proves

## 1. What a handle is

A handle is the name one person is known by to the other — `RedQuE3n`, not
`red`, and not a GUID. It is chosen once, stored on the machine that owns it,
and carried in `Hello` (`Pegasus_Sync.md` §3) so the far side can say who it is
talking to.

The grammar is deliberately narrow, because a handle is read aloud and retyped:

    3 to 20 characters
    letters, digits, hyphen and underscore
    must begin with a letter

Comparison is case-insensitive and the display form is preserved. `RedQuE3n`
and `redque3n` are the same account; the file is named by the folded form and
the file's own `handle` line carries the capitalisation to show. This is the
rule AIM used, and it is the right one for a name people say out loud.

Before this existed, identity was `Environment.UserName` and a `PeerId` minted
by `Guid.NewGuid()` **on every launch** — so a peer had no stable identity at
all, not even a wrong one. §6 covers what replaced it.

## 2. What signing in proves, and what it does not

Signing in unlocks a private key held on your own disk. That is the whole of
it, and the distinction matters:

- It proves **to your own machine** that you can decrypt the key stored under
  that handle. Wrong password, no key, no sign-in.
- It does **not** prove anything to the far peer. The handle in `Hello` is
  asserted, not demonstrated. Anyone holding the join code can connect and
  claim to be `RedQuE3n`.

There is no server to check a password against, and by design there is not
going to be one for this (`Pegasus_Sync.md` §1). A password alone would
therefore have bought nothing: with no third party to verify it, it is either
theatre or a permanent shared secret weaker than the rotating join code it sat
beside. What makes a handle mean something without a server is a keypair, and
the password's only job is to protect that keypair at rest.

The keypair is generated, stored and loaded now, and `Identity.Sign` is
exercised by the suite, but **nothing on the wire is signed yet**. Binding the
handle to the key across a connection — a signed challenge and a pinned public
key on first contact — is the next pass. Until it lands, treat the displayed
handle as a convenience, not as authentication. Stated as a hazard rather than
a behaviour, in the sense `Pegasus_Design.md` §11 uses the word.

A second hazard in the same category: `Version.Protocol` is declared and never
put on the wire, so there is no version negotiation. Two peers running
different builds discover it as a decode failure, not as a diagnosis. Adding
the handle to `Hello` changed the meaning of that frame without any mechanism
to detect the mismatch.

## 3. The identity file

One file per handle, under `<LocalApplicationData>/Pegasus/identity/`, named by
the folded handle with a `.id` extension. It is line-oriented text, one
`key value` pair per line, so `cat` is a sufficient tool for seeing what is
stored and what is not:

    pegasus-identity 1
    handle RedQuE3n
    created 2026-08-09T23:41:07Z
    public <base64 SubjectPublicKeyInfo>
    kdf pbkdf2-sha256 210000 <base64 salt>
    secret <base64 nonce || ciphertext || tag>

Everything on those lines is public except `secret`, which is the PKCS#8
private key sealed under the password-derived key (§4). The file is created
with owner-only permissions where the platform has them.

Text rather than a binary blob because a human may need to answer "is my key
actually encrypted in there" without a debugger, and the answer should be
readable.

A test asserts the private key does not appear in the file, and the first
version of it was worthless. It compared the raw PKCS#8 bytes against the file's
bytes — but the file stores base64, so the two could never match no matter what
was written. Run against a build deliberately altered to store the key
*unsealed*, the guard passed. The corrected test compares base64 against base64
and additionally pins the sealed length at exactly a nonce and a tag longer than
the key it wraps; against the same sabotage it fails on both counts.

This is recorded rather than quietly fixed because it is an instance of the
argument in `Pegasus_Design.md` §11: a guard nobody has watched fail is not
evidence of anything. It cost one run of the sabotage to find out.

This is not the `.pegasus` format (`Pegasus_Format.md`). An identity is written
once and read many times; it has no append log, no compaction and no torn-write
recovery, because there is no stream of updates to recover.

## 4. Deriving the key from the password

PBKDF2-HMAC-SHA256, 210,000 iterations, 32-byte key, then AES-256-GCM through
the same `Crypto.seal` the wire uses. No new primitive is introduced for this.

**The salt is random per identity, and stored in the file.** That is the
opposite of `Pegasus_Sync.md` §5, where the salt is fixed and openly called a
weakness, and the difference is worth stating because the two look like the
same code doing the same thing:

|  | join code | identity password |
|---|---|---|
| who derives the key | both peers, independently | one machine, its owner |
| may they exchange a salt first | no — there is no channel yet | no need — it is stored beside the ciphertext |
| consequence | fixed salt, precomputable | random salt, not precomputable |

A fixed salt is forced there by the requirement that two parties reach the same
key with no round trip. No such requirement exists here, so nothing forces the
weakness, and taking it anyway would be carelessness rather than a trade.

A wrong password surfaces as GCM authentication failure. `Crypto.tryOpenSealed`
returns an option so that the identity path can answer "wrong password" without
catching an exception whose message talks about join codes; `Crypto.openSealed`
remains the raising form the session uses.

What this does not defend against: someone who already has your disk *and* your
password, and offline guessing by anyone who takes the file, slowed by the
iteration count and nothing else. There is no rate limit, because there is
nothing running to enforce one.

## 5. Why ECDSA P-256 rather than Ed25519

Ed25519 is the better choice on the merits and is not available: .NET 10 ships
no Ed25519 in `System.Security.Cryptography` — verified by enumerating the
namespace, which offers `ECDsa` and the post-quantum `MLDsa` and nothing in
between. Reaching it would mean NSec or BouncyCastle, and a new dependency is a
decision that has to earn itself.

It does not earn itself here. P-256 with SHA-256 is in the framework, is
FIPS-blessed, exports as `SubjectPublicKeyInfo` and `Pkcs8PrivateKey` — both
standard encodings any other language can read — and is entirely adequate for
binding a handle between two people. The known sharp edge of ECDSA, nonce
reuse leaking the private key, sits inside the platform implementation and not
in code written here.

If a future pass wants Ed25519, the file format carries a `public` line and a
`kdf` line and nothing that assumes a curve; the migration is a version bump on
line 1.

## 6. The fingerprint, and why it is not the Yjs client id

`PeerId` is now the first 8 bytes of SHA-256 over the public key's
`SubjectPublicKeyInfo`, as 16 lowercase hex characters. It is derived, not
drawn, so it is stable across launches — which is the concrete thing that was
broken before, where every restart produced a new `PeerId`.

The caret colour is derived from the same bytes, indexed into a fixed palette
rather than computed in a colour space, so two identities are visually distinct
without any risk of landing on an unreadable tint.

**The Yjs client id is not derived from it, and must not be.** They answer
different questions: `PeerId` names a *person*, and a client id names a
*replica*. One person may hold two replicas — a laptop and a desktop, both
signed in as `RedQuE3n` — and giving those the same client id is precisely the
failure `Pegasus_Design.md` §4.5 demonstrates, where colliding ids make a merge
silently keep one side's edits and discard the other's. §4.7 additionally
constrains a client id to below 2^32, which truncating a fingerprint would
satisfy while quietly reintroducing §4.5. `ClientId.fresh` stays random per
document and is untouched by any of this.

## 7. What this pass deliberately does not do

- **No signed handshake.** §2. The handle is asserted.
- **No contacts and no key pinning.** Nothing records that `RedQuE3n`'s key was
  a particular value last time, so nothing can notice it changed.
- **No password change, and no recovery.** Lose the password and the identity
  is gone; the notes are not, because they are not encrypted with it.
- **No account server, no buddy list, no connect-by-handle.** Pairing is still
  address, port and join code (`Pegasus_Sync.md` §2). A relay remains deferred,
  and when it arrives it should authenticate these same keys rather than invent
  a second credential.
- **The workspace is not partitioned by handle.** Notes live where they always
  have and every identity on a machine sees the same ones. A handle says who
  you are to your peer; it is not a separate account of files. Partitioning
  would strand every note written before this feature existed, for no benefit
  to two people who each own their own machine.
