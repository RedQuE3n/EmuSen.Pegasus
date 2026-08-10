namespace EmuSen.Pegasus

open System

/// What a machine already believed about the peer that just connected.
type Trust =
    /// Never seen this handle before. The key has been written down, and any
    /// later key for the same handle will be refused against it.
    | FirstSight
    /// The same key as last time.
    | Recognised
    /// The handle is known and the key is not the one pinned for it.
    | Impostor of pinned: PeerId * offered: PeerId

/// Trust on first use: the first key seen for a handle is written down, and a
/// different key claiming that handle later is refused.
///
/// This is the half of the identity problem a signature cannot solve on its own.
/// Attestation proves the far side holds the key whose fingerprint it claims; it
/// cannot know whether that key is the person you meant, because there is no
/// authority to ask and this project is deliberately not going to run one.
/// Pinning turns that unanswerable question into an answerable one: not "is this
/// RedQuE3n" but "is this the same RedQuE3n as last time".
///
/// What it does not defend against is the first connection. If an impostor is
/// there the very first time, it gets pinned and every later session with the
/// real person is the one that looks wrong. The mitigation is out of band and
/// human: read the fingerprint aloud once, which is why the shell shows it.
///
/// Rows in identity.db rather than a file of its own. First-sighting-wins used
/// to be a fold over an append-only file in order, which is a constraint written
/// as an algorithm; it is now PRIMARY KEY (owner, handle) with INSERT OR IGNORE,
/// where the database refuses the second write and there is nothing left to get
/// wrong. The `owner` column is why one machine's two identities do not share
/// one contact list.
module KnownPeers =

    /// Records a first sighting and reports what was already known.
    ///
    /// The fingerprint comes from the key rather than from the PeerInfo beside
    /// it. Those two agreeing is Attestation's job, and this should not be the
    /// second place that assumption lives.
    ///
    /// The insert is attempted before the read, so the "is it known" question
    /// and the "write it down" answer cannot interleave with another connection
    /// arriving between them. INSERT OR IGNORE reports how many rows it wrote,
    /// which is exactly the first-sight answer.
    let trust (root: string) (local: Handle) (peer: PeerInfo) (publicKey: byte[]) =
        let offered = Fingerprint.ofPublicKey publicKey
        use db = Db.openAt (IdentityStore.databaseIn root)

        let inserted =
            Db.executeWith
                db
                "INSERT OR IGNORE INTO known_peers (owner, handle, fingerprint, public_key, first_seen)
                 VALUES ($owner, $handle, $fingerprint, $public, $seen)"
                [ "$owner", box local.Folded
                  "$handle", box peer.Handle.Folded
                  "$fingerprint", box offered.Value
                  "$public", box publicKey
                  "$seen", box (DateTime.UtcNow.ToString "o") ]

        if inserted = 1 then
            FirstSight
        else
            let pinned =
                Db.query
                    db
                    "SELECT fingerprint FROM known_peers WHERE owner = $owner AND handle = $handle"
                    [ "$owner", box local.Folded; "$handle", box peer.Handle.Folded ]
                    (fun r -> PeerId(r.GetString 0))

            match pinned with
            | [ known ] when known = offered -> Recognised
            | [ known ] -> Impostor(known, offered)
            // The insert was ignored, so a row exists; not finding it means the
            // store changed underneath us, which is not a thing to guess about.
            | _ -> Impostor(PeerId "unknown", offered)

    /// Every peer this identity has pinned, for showing a user what their
    /// machine believes.
    let pinnedFor (root: string) (local: Handle) =
        use db = Db.openAt (IdentityStore.databaseIn root)

        Db.query
            db
            "SELECT handle, fingerprint FROM known_peers WHERE owner = $owner ORDER BY handle"
            [ "$owner", box local.Folded ]
            (fun r -> r.GetString 0, PeerId(r.GetString 1))
        |> List.toArray
