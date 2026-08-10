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
and `redque3n` are the same account; the folded form is the primary key of the
`identities` table and a `display` column carries the capitalisation to show.
This is the rule AIM used, and it is the right one for a name people say aloud.

Before this existed, identity was `Environment.UserName` and a `PeerId` minted
by `Guid.NewGuid()` **on every launch** — so a peer had no stable identity at
all, not even a wrong one. §6 covers what replaced it.

## 2. What signing in proves, and what it does not

Signing in unlocks a private key held on your own disk. That is all it does
locally, and the distinction is worth keeping: the password proves something to
**your own machine**, and it is the key — not the password — that proves
anything to the far peer.

There is no server to check a password against, and by design there is not going
to be one for this (`Pegasus_Sync.md` §1). A password alone would therefore have
bought nothing: with no third party to verify it, it is either theatre or a
permanent shared secret weaker than the rotating join code it sits beside. What
makes a handle mean something without a server is a keypair, and the password's
only job is to protect that keypair at rest.

**An earlier version of this section said the handle in `Hello` was asserted and
never demonstrated, and recorded that as a hazard.** That is no longer true, and
the rest of this section is what replaced it.

The keypair is now used. Each side sends its public key in `Hello` and a random
nonce in `Challenge`, and signs the nonce the other sent. A `Proof` is checked
against the key that arrived, and the claimed `PeerId` must be that key's
fingerprint. Nothing that touches the document — no `SyncStep1`, no `Update`, no
subscription to local edits — happens until that has verified, so a peer who
fails the proof learns nothing and changes nothing.

The exchange is mutual and symmetric: neither end is trusted first, and neither
has to be the host, which is what keeps it working unchanged when the far side
is a relay rather than a person.

**What is signed matters as much as that it is signed.** The payload is a domain
tag, the verifier's nonce, and the signer's own fingerprint. The tag stops a
signature made here being presented as a signature for some other purpose later.
The nonce makes a proof good for one session, so one recorded from an earlier
session is useless. The fingerprint binds the signature to the identity being
claimed.

The check that is easy to omit is the fingerprint one, and omitting it defeats
the whole exchange: an impostor can present **its own** key beside the victim's
`PeerId` and sign the challenge perfectly, because it does hold the key it sent.
Verifying only the signature passes it. What refuses it is insisting the claimed
id be the fingerprint of the key presented. A test drives exactly that forgery.

Two things this still does not do:

- **It does not say the key is the person you meant.** That is `KnownPeers`,
  §7 — trust on first use, which turns an unanswerable question into an
  answerable one: not "is this RedQuE3n" but "is this the same RedQuE3n as last
  time". The first connection is still unprotected, and the mitigation is
  reading a fingerprint aloud, which is why the shell shows one.
- **It does not help against someone who has the join code and is present for
  the first connection.** They will be pinned as the peer. `Pegasus_Sync.md` §5
  already says the join code is not a security boundary.

The protocol-version hazard is also closed. `Version.Protocol` is 2 and is now
actually sent, in `Hello`, which it never was at 1. A peer running a different
build is told so in the first frame instead of discovering it as a decode
failure somewhere further down.

## 3. The identity store

Identities are rows in `identity.db`, a SQLite database under
`<LocalApplicationData>/Pegasus/identity/`:

    CREATE TABLE identities (
        handle      TEXT PRIMARY KEY,   -- folded
        display     TEXT NOT NULL,      -- the capitalisation to show
        created     TEXT NOT NULL,
        public_key  BLOB NOT NULL,      -- SubjectPublicKeyInfo
        kdf         TEXT NOT NULL,
        iterations  INTEGER NOT NULL,
        salt        BLOB NOT NULL,
        secret      BLOB NOT NULL       -- PKCS#8, sealed under the password
    )

Everything is public except `secret`. The folded handle is the primary key, so
`RedQuE3n` and `redque3n` cannot become two accounts by construction rather than
by a check somebody has to remember to write.

### 3.1 It was a text file, and this is what changed

Until this pass each identity was one line-oriented text file, `<handle>.id`,
and the reason given was that somebody should be able to answer "is my key
actually encrypted in there" with `cat` and no debugger.

That property is genuinely weaker now. The answer is one tool further away:

    sqlite3 identity.db 'select handle, hex(secret) from identities'

It was given up deliberately, for two reasons. The first is that constraints
beat parsing: the "first key seen for a handle wins" rule in §7 was a fold
over an append-only file in order — a constraint expressed as an algorithm — and
is now a primary key with `INSERT OR IGNORE`, where the database refuses the
second write and there is nothing left to get wrong. The second is that Chariot
needs the same tables, and one storage mechanism across both is one to reason
about rather than two.

The guard moved across and got better. It asserts the exported private key bytes
appear in **no file** under the store's directory — not just `identity.db`,
because a journal or WAL companion would hold the same row — and separately that
the stored blob is exactly a nonce and a tag longer than what it wraps. Against
a build altered to store the key unsealed it fails on the first count naming the
file. The old text version of this guard compared raw PKCS#8 bytes against a
base64 file and could never have matched anything, which is recorded in the
history of this section as the reason guards get sabotaged before they are
believed.

### 3.2 The advisory this dependency arrives with

`Microsoft.Data.Sqlite` 10.0.10 resolves `SQLitePCLRaw.lib.e_sqlite3` 2.1.11
transitively, and that package carries a known high-severity advisory,
GHSA-2m69-gcr7-jv3q. This is the **same advisory** cited in
`Chariot_Design.md` §2.2 as one of the things wrong with the forked C# server,
so taking it silently here would have been indefensible.

Pinning `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5 explicitly overrides the
transitive version; the 3.x bundle drops `lib.e_sqlite3` entirely in favour of
`config.e_sqlite3`, and a vulnerability audit of the application comes back
clean. EmuSen pins the identical pair in `EmuSen.csproj` and
`EmuSen.Pharaoh.csproj`, where the pin is described as deliberate but the reason
is not written down anywhere in that repository. It is written down here.

Keep the two pins in step. A bare `Microsoft.Data.Sqlite` reference silently
reintroduces the advisory.

### 3.3 What this is not

This is not the `.pegasus` format (`Pegasus_Format.md`), which still holds notes
and is unchanged by this pass. The distinction is the one that decides where
anything belongs: **a note is a CRDT and stays one**, because two people editing
the same sentence at once is a merge problem no table solves. Identities and
pinned keys are ordinary local bookkeeping with no concurrent writer, and that
is rows.

The same reasoning protects the workspace index. It is stored as a Pegasus
document and must remain one — `Pegasus_Sync.md` §6 calls it the design's one
piece of genuine economy, because note creation and rename are concurrent
operations between peers and get conflict resolution, crash recovery and the
sync path free by being a document. If note storage ever moves into SQLite, what
moves is where the update blobs live, not what the data model is.

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

## 7. Trust on first use

A signature proves the far side holds the key whose fingerprint it claims. It
cannot say whether that key belongs to the person you meant, because there is no
authority to ask and this project is deliberately not going to run one.

`KnownPeers` turns that unanswerable question into an answerable one — not "is
this RedQuE3n" but "is this the same RedQuE3n as last time":

    CREATE TABLE known_peers (
        owner       TEXT NOT NULL,      -- folded handle of whoever is signed in
        handle      TEXT NOT NULL,      -- folded handle of the peer
        fingerprint TEXT NOT NULL,
        public_key  BLOB NOT NULL,
        first_seen  TEXT NOT NULL,
        PRIMARY KEY (owner, handle)
    )

The first key seen for a handle is written down and a different key claiming
that handle afterwards is refused. `INSERT OR IGNORE` attempts the write before
asking what is known, so the question and the answer cannot interleave with
another connection arriving between them, and the number of rows written *is*
the first-sight answer.

The `owner` column is why signing in under a second handle on one machine does
not inherit the first one's pins. One identity's trust decisions are not
another's.

**Being refused must also not overwrite the pin.** If a rejected key replaced
the stored one, the real peer would be refused ever afterwards and the impostor
would become the recognised party — the failure would invert. Changing the
statement to `INSERT OR REPLACE` turns five tests red, which is the constraint
doing the work rather than a rule somebody has to remember.

What this does not defend against is the **first** connection. An impostor
present the first time gets pinned, and every later session with the real person
is the one that looks wrong. The mitigation is out of band and human: read the
fingerprint aloud once. That is why the shell shows it beside the handle.

Recovery is deliberately manual. A peer who genuinely reinstalls presents a new
key and is refused until somebody deletes the row, because a changed key is
either "they reinstalled" or "this is not them" and only a person can tell
which. The refusal message names both fingerprints so that person has what they
need to decide.

## 8. What this pass deliberately does not do

- **No account server, no buddy list, no connect-by-handle.** Pairing is still
  address, port and join code (`Pegasus_Sync.md` §2). That is Chariot, and when
  it arrives it authenticates these same keys rather than inventing a second
  credential.
- **No password change, and no recovery.** Lose the password and the identity is
  gone; the notes are not, because they are not encrypted with it.
- **No key rotation.** There is no way to say "this is still me, with a new
  key" other than deleting the pin by hand.
- **The workspace is not partitioned by handle.** Notes live where they always
  have and every identity on a machine sees the same ones. A handle says who you
  are to your peer; it is not a separate account of files. Partitioning would
  strand every note written before this feature existed, for no benefit to two
  people who each own their own machine.
- **Notes are not in SQLite.** §3.3 explains why the `.pegasus` format stays
  where it is, and what would and would not move if it ever changed.
