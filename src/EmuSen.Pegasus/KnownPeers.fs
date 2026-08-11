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

    /// Takes a card from the relay's directory, or says why it was refused.
    ///
    /// BOTH CHECKS OR NEITHER, and the order matters less than the fact that
    /// there are two. `Messaging.verifyCard` proves the card is internally
    /// consistent -- the messaging key really was signed by the identity key
    /// printed beside it -- and on its own that is worth nothing, because a
    /// relay wanting to read somebody's post would forge the whole card, not
    /// half of one. What makes the directory safe to use is the second check:
    /// the identity key in the card must be the key already PINNED for that
    /// handle, so a relay handing out a card of its own invention is caught by
    /// the same rule that catches a person whose key changed.
    ///
    /// The messaging key is updated when the identity matches, rather than
    /// pinned once and frozen. That is not a weakening: an update takes a
    /// signature from the pinned identity key, which only its holder can
    /// produce. It is also REQUIRED rather than merely allowed -- an identity
    /// created before messaging existed mints its messaging key the first time
    /// it is unlocked (IdentityStore.unlock), so its card legitimately changes
    /// while its identity stays exactly what everybody pinned.
    let acceptCard (root: string) (local: Handle) (card: Card) =
        if not (Messaging.verifyCard card) then
            Error $"{card.Handle.Value} sent a messaging key its identity key did not sign"
        else
            let offered = Fingerprint.ofPublicKey card.Identity
            use db = Db.openAt (IdentityStore.databaseIn root)

            let inserted =
                Db.executeWith
                    db
                    "INSERT OR IGNORE INTO known_peers (owner, handle, fingerprint, public_key, first_seen, message_key)
                     VALUES ($owner, $handle, $fingerprint, $public, $seen, $messaging)"
                    [ "$owner", box local.Folded
                      "$handle", box card.Handle.Folded
                      "$fingerprint", box offered.Value
                      "$public", box card.Identity
                      "$seen", box (DateTime.UtcNow.ToString "o")
                      "$messaging", box card.Messaging ]

            if inserted = 1 then
                Ok card
            else
                let pinned =
                    Db.query
                        db
                        "SELECT fingerprint FROM known_peers WHERE owner = $owner AND handle = $handle"
                        [ "$owner", box local.Folded; "$handle", box card.Handle.Folded ]
                        (fun r -> PeerId(r.GetString 0))

                match pinned with
                | [ known ] when known = offered ->
                    Db.executeWith
                        db
                        "UPDATE known_peers SET message_key = $messaging WHERE owner = $owner AND handle = $handle"
                        [ "$messaging", box card.Messaging
                          "$owner", box local.Folded
                          "$handle", box card.Handle.Folded ]
                    |> ignore

                    Ok card
                | [ known ] ->
                    Error(
                        $"{card.Handle.Value} is presenting identity key {offered.Value}, but {known.Value} was pinned "
                        + "for that handle. Either they have a new identity file, or this is not them."
                    )
                | _ -> Error $"the store no longer holds the key it pinned for {card.Handle.Value}"

    /// The messaging key pinned for a peer, or None if this machine has never
    /// accepted a card for them.
    ///
    /// None is an ordinary answer rather than a failure: it means "ask the
    /// relay for their card first", which is what a message cannot be sealed
    /// without. A caller that treated it as an error would refuse to start the
    /// very conversation that fixes it.
    let messagingKeyFor (root: string) (local: Handle) (peer: Handle) =
        use db = Db.openAt (IdentityStore.databaseIn root)

        Db.query
            db
            "SELECT message_key FROM known_peers WHERE owner = $owner AND handle = $handle AND message_key IS NOT NULL"
            [ "$owner", box local.Folded; "$handle", box peer.Folded ]
            (fun r -> r.GetFieldValue<byte[]> 0)
        |> List.tryHead
