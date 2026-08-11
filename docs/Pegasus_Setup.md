# Pegasus setup: installing the programs and standing up a relay

This is the operational man page. `README.md` covers building from source, which
is one `dotnet build`; this covers the released binaries, what has to be
reachable on the network, where state is kept, and how to run `EmuSen.Chariot`
as something other than a foreground process.

Everything here was checked against the 0.1.0 binaries rather than reasoned
about, except where it says otherwise — and where a claim could not be checked,
it says so rather than sounding confident. §11 collects the untested ones.

## 1. What you need, and what you do not

Two people, and one of the following:

- **A route between the two machines.** Same LAN is the ordinary case.
- **Or a machine both can reach**, running Chariot. That machine never learns
  what is in your notes (§5), so it can be a cheap VPS.

You do **not** need .NET installed. The releases are self-contained: the
runtime, Avalonia, SQLite and the Yrs CRDT native are inside the executable.
That is why a 100 MB program is a 45 MB download, and it is the reason there is
no "install .NET first" step to get wrong.

You do not need an account anywhere. There is no service behind Pegasus, no
sign-up, and deliberately never will be — a password here unlocks a keypair on
your own disk and proves nothing to anybody (`Pegasus_Identity.md` §2).

## 2. Getting the binaries

Releases are at `github.com/RedQuE3n/EmuSen.Pegasus/releases` and
`github.com/RedQuE3n/EmuSen.Chariot/releases`. Both ship four builds: Linux x64,
macOS Apple Silicon, macOS Intel, Windows x64. There is **no Linux arm64 build**
and no 32-bit anything.

Check what you downloaded before running it. Every release carries `SHA256SUMS`:

    sha256sum -c SHA256SUMS --ignore-missing

This is worth doing for a reason specific to these releases rather than as a
ritual: **the binaries are not signed**, so the usual operating-system check does
not happen, and the checksum is the only integrity evidence there is. Signing
needs a paid certificate from Apple or a code-signing CA, and there is not one.

## 2.1 Getting past the operating system

Because nothing is signed, each platform will object once.

**Linux** objects to nothing. Extract, mark executable if your extractor did not,
run it:

    tar xzf Pegasus-0.1.0-linux-x64.tar.gz
    ./Pegasus-linux-x64/EmuSen.Pegasus

**macOS** attaches a quarantine flag to anything downloaded, and Gatekeeper
refuses unsigned quarantined code with a dialog that offers no way past it.
Strip the flag:

    tar xzf Pegasus-0.1.0-osx-arm64.tar.gz
    xattr -dr com.apple.quarantine Pegasus-osx-arm64/EmuSen.Pegasus.app
    open Pegasus-osx-arm64/EmuSen.Pegasus.app

The `.app` is a real bundle — `Contents/MacOS`, `Contents/Info.plist`, bundle id
`io.github.redque3n.pegasus` — so it launches from Finder and appears in the Dock
like anything else. It has **no icon**, because nothing in the repository has
ever had one, so the generic application icon is what you get. Chariot on macOS
is a plain binary, not a bundle, because it is a daemon and has no window.

**Windows** shows "Windows protected your PC". The binary is behind *More
info → Run anyway*. There is no way to remove that prompt short of signing.

## 3. First run: who you are

Pegasus opens on a sign-in window. Type a handle — the name your peer sees, such
as `RedQuE3n` — and a password, then click **Create**. Afterwards, **Sign in**
with the same pair; identities already on the machine are listed, so the handle
only has to be typed once.

**What the password does is narrower than it looks**, and it is worth
understanding before you rely on it. It unlocks a keypair sitting on this disk.
It is not checked against a server, because there is no server in this picture
to check it against. Anyone with your disk and your password has your identity;
anyone with your disk and *not* your password has an encrypted blob.
`Pegasus_Identity.md` §2 and §3 are the long version, including a correction
about a guard that turned out to protect nothing.

The handle is proven to your peer — each side signs a challenge, and a peer whose
key changes under a known handle is refused rather than quietly accepted
(`Pegasus_Identity.md` §7). What that establishes is *continuity*, not identity:
it proves the same party as last time, and the first time it proves nothing at
all.

## 4. Pairing directly

Nothing in the middle. One person opens a note and clicks **Host**, which shows
an address, a port and a join code such as `7-lantern-quartz`. The other types
all three and clicks **Join**.

**The port changes every session.** Hosting asks the operating system for a free
port rather than taking a fixed one, so there is nothing to configure and nothing
to collide with — but there is also nothing stable to write in a firewall rule,
which is §8's problem.

Two limits, stated here because they are easier to meet than to diagnose:

- **A host accepts exactly one joiner.** A session is a pair, not a group. This
  is a property of what ships rather than of the frame format
  (`Pegasus_Sync.md` §1).
- **Opening another note drops the connection**, because a live conversation
  holds the document it started with. The window does the disconnect for you.

## 5. Pairing through a relay

If you run Chariot (§6), type its address, port and passphrase into the buddy
panel and click **Sign in**. Everyone else signed in appears in the list. Agree a
join code between yourselves, type it into the box at the top, select each other
and click **Open note**. The server is remembered for next time
(`Pegasus_Identity.md` §8).

**The join code does not go away when you use a relay, and that is the whole
design.** It is the key your notes are sealed under, and Chariot has no way to
derive it — a relay that could read your notes would be a different program.
What the relay saves you is the address and the port, which were the two things
that changed every session. `Pegasus_Sync.md` §4.1 has the exchange;
`Chariot_Design.md` §5 is routing without reading.

So: the passphrase in the buddy panel and the join code in the box are **not the
same secret and do not protect the same thing**. The passphrase decides who may
open a session with your server. The join code decides who can read the note.
Giving somebody the passphrase lets them onto the relay; it does not let them
into your notes.

## 6. Running Chariot

Chariot is a daemon and behaves like one: options on the command line, the secret
in the environment, status on stdout, refusals on stderr, non-zero exit when it
will not start.

    CHARIOT_PASSPHRASE=your-secret ./EmuSen.Chariot --port 7420 --db chariot.db

| | |
|---|---|
| `--port N` | Listening port. Default **7420**. |
| `--db PATH` | Accounts and queued post. Default **`chariot.db`** in the working directory. |
| `--help` | Prints usage and exits **2**. |
| `CHARIOT_PASSPHRASE` | Required. No default, and it refuses to start without one. |
| `CHARIOT_HANDLE` | The name it signs in as. Default `chariot`. |

On success it prints the port, the database, and its own fingerprint:

    chariot listening on 7420, accounts in chariot.db
    signing in as chariot, key 48de15a485f69deb

**The fingerprint on that second line is the thing to keep.** It is the only
value an operator can read aloud to somebody whose client is refusing to
connect, and a refusal means the pinned key changed — only a person can tell
"we rebuilt the server" from "that is not our server".

Some behaviour worth knowing before it surprises you:

- **The passphrase is an environment variable and never an argument.** Anything
  in `argv` is visible to every process on the machine through `ps`, and a shared
  secret that leaks to anyone who can list processes is not a secret. There is no
  `--passphrase` flag and adding one would be a mistake.
- **`--db` creates missing parent directories.** `--db /var/lib/chariot/chariot.db`
  works on a path that does not exist yet; verified against the 0.1.0 binary.
- **The handle is fixed on first run.** Starting the same database under a
  different `CHARIOT_HANDLE` is refused, and the refusal explains itself:

      chariot will not start: this database belongs to a server called chariot,
      and it was asked to run as someoneelse. Clients have pinned the first name
      against this key; running under the second would look to every one of them
      like a different server. Use the original name, or a different database.

- **Accounts are trust on first use.** The first public key to claim a handle
  owns it, and a later mismatch is refused rather than replaced. There is no
  registration step and nothing to provision. The weakness is the obvious one: a
  handle claimed by an impostor *before* its owner ever connects belongs to the
  impostor from then on, and nothing at this layer can tell the difference
  (`Chariot_Design.md` §7).
- **Ctrl-C is a request to stop, not a crash.** It cancels the accept loop, prints
  `chariot stopped`, and exits 0.

## 7. Keeping Chariot running

A foreground process in a terminal is fine for trying it. For anything longer,
run it under whatever supervises services on that machine.

On Linux, a systemd unit:

    [Unit]
    Description=Chariot — Pegasus relay
    After=network-online.target

    [Service]
    ExecStart=/opt/chariot/EmuSen.Chariot --port 7420 --db /var/lib/chariot/chariot.db
    EnvironmentFile=/etc/chariot/chariot.env
    Restart=on-failure
    User=chariot
    StateDirectory=chariot

    [Install]
    WantedBy=multi-user.target

with `/etc/chariot/chariot.env` containing `CHARIOT_PASSPHRASE=...`.

**Put the passphrase in that file rather than in `Environment=` in the unit**,
and make it `chmod 600` owned by root. Unit files are world-readable by default,
and a secret in one is a secret every user on the box can read — which would
give away exactly the property §6 protects by keeping the passphrase out of
`argv`. `systemd-creds` is better still if it is available.

There is no equivalent recipe here for a Windows service or a macOS
`launchd` job. Both are ordinary to write and neither has been tried, so they are
absent rather than guessed at.

## 8. The network

Chariot needs **one inbound TCP port**, whatever `--port` says, default 7420.
That is the whole of it: no web stack, no second port for an API, nothing that
speaks HTTP. §3 of `Chariot_Design.md` records what dropping the web stack bought
and what it deferred.

Direct pairing (§4) is harder to firewall, because **the host's port is assigned
by the operating system and differs every session**. On a home LAN this rarely
matters. Across anything with NAT in the way it matters a great deal, and the
honest answer is that direct pairing is a same-network arrangement: there is no
hole punching, no UPnP, and no STUN. Reaching somebody across the internet is
what the relay is for.

WebSocket transport was deferred rather than rejected, and it is the thing that
would make this friendlier to NAT and to corporate proxies. The frame layout is
transport-agnostic so that argument stays about transport
(`Chariot_Design.md` §3).

## 9. What is on disk

Both programs keep everything under one root, and neither writes anywhere else.

**Pegasus** uses `SpecialFolder.LocalApplicationData`:

| | |
|---|---|
| Notes | `<LocalApplicationData>/Pegasus/workspace` — one `.pegasus` file per note |
| Identities and pinned peer keys | `<LocalApplicationData>/Pegasus/identity/identity.db` |

On Linux that root is `~/.local/share`. On Windows it is `%LOCALAPPDATA%`,
normally `C:\Users\<you>\AppData\Local`. **On macOS .NET also maps it to
`~/.local/share`**, not `~/Library/Application Support` — which surprises people
who go looking in the Mac location and find nothing. That mapping is .NET's
behaviour on Unix generally; it has not been confirmed on a Mac here (§11).

A `.md` projection sits beside every note, regenerated and never read back, so
notes are readable in any editor without Pegasus running
(`Pegasus_Format.md` §5). The workspace is deliberately **not** partitioned by
handle: every identity on a machine sees the same notes, because a handle says
who you are to your peer rather than opening a separate drawer of files.

**Chariot** keeps one SQLite database, wherever `--db` points: accounts, and post
for peers who were not connected. The queued payloads are sealed under a join
code the server does not have, so they are stored as the opaque blobs they are
(`Chariot_Design.md` §6).

To move a machine or take a backup, copy the roots. Copying `identity.db` moves
your identity, and it is the file whose loss cannot be recovered from — there is
no reset, because there is no authority to ask for one.

## 10. Upgrading and removing

Upgrading is replacing the executable. Nothing in either program writes a version
into its state, and neither has a migration to run on the 0.1.0 line.

Removing is deleting the executable and, if you mean it, the roots in §9.
Nothing is installed elsewhere: no registry keys, no `/usr/lib` drop, no
launch agent, nothing in `~/Library`. This falls out of shipping a self-contained
single file rather than being a feature anyone implemented.

**Two versions that must not disagree**: this page describes application 0.1.0.
The wire protocol is **4**, carried by `EmuSen.Pegasus.Core` 0.3.0, and it is the
protocol number in `Hello` — not the version on the download — that decides
whether two programs can talk. A Pegasus and a Chariot on different application
versions are fine as long as the protocol matches, and `Hello` says so in the
first frame rather than letting it be discovered as a decode failure further down.

## 11. What this setup does not give you

Collected rather than scattered, because a reader deciding whether to use this
deserves them in one place.

- **The macOS and Windows binaries have never been run.** They were
  cross-compiled on Linux from identical source by the same SDK, and their
  natives are present in the bundles, but nobody has started them. The Linux
  builds of both programs were launched and exercised. This is the largest gap
  on this page and it is not hidden in it.
- **The `~/.local/share` path on macOS is inferred**, from .NET's Unix behaviour,
  not observed on a Mac.
- **Nothing is signed or notarised**, so every platform warns once and the
  checksum is the only integrity evidence (§2).
- **The join code is a pre-shared key with a fixed salt.** Both peers must reach
  the same key from a spoken code with no round trip in which to agree a random
  salt, so an attacker can precompute against the whole ~9,216-code space. This
  is two people on a private network agreeing a short code, and it is not
  protection against somebody who watched the pairing. There is no forward
  secrecy on that path. `Pegasus_Sync.md` §5 is the measurement and the threat
  model.
- **Trust on first use, on both sides.** Peer keys and Chariot accounts are both
  first-claim-wins. A key that changes is refused; a key that was wrong from the
  start is never detected.
- **A direct host takes one joiner**, and direct pairing does not cross NAT (§8).
- **No Linux arm64 build**, so a Raspberry Pi cannot run the released Chariot.
  Building from source on the machine works and is one `dotnet publish`.
