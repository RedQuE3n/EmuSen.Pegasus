namespace EmuSen.Pegasus

open System

/// The saved buddy list: who this identity decided to keep.
///
/// THIS IS NOT THE ROSTER AND THE DIFFERENCE IS THE WHOLE POINT. The roster is
/// what Chariot pushes — everybody signed in to that server this second — and it
/// arrives whole, empties when the connection drops, and belongs to the
/// connection rather than to the user. This is a list on disk that outlives all
/// of that.
///
/// The consequence worth stating, because it is the one a buddy list exists
/// for: **you cannot show that somebody is offline unless you were already
/// listing them.** A roster can only ever say who is present, so a window built
/// on the roster alone shows an empty panel whether your friend is asleep or
/// whether you have no friends. Every messenger since AIM has kept the two
/// separate for exactly this reason, and the presence dot is the roster painted
/// onto this list by matching handles.
///
/// Owner-scoped like known_peers and servers: two identities on one machine are
/// two people, and one's buddies are not the other's.
module Friends =

    /// Adds somebody, or does nothing if they are already there.
    ///
    /// INSERT OR IGNORE rather than a read followed by a write, for the reason
    /// the whole store keeps reaching for it: the primary key is the constraint,
    /// so "already a friend" is answered by the database refusing the row rather
    /// than by a check that could interleave with anything.
    ///
    /// The display form is stored beside the folded one so the list shows
    /// "RedQuE3n" and matches on "redque3n", which is the rule handles follow
    /// everywhere else (Types.fs) and would be a surprise to break here.
    let add (root: string) (local: Handle) (peer: Handle) =
        use db = Db.openAt (IdentityStore.databaseIn root)

        Db.executeWith
            db
            "INSERT OR IGNORE INTO friends (owner, handle, display, added_at)
             VALUES ($owner, $handle, $display, $added)"
            [ "$owner", box local.Folded
              "$handle", box peer.Folded
              "$display", box peer.Value
              "$added", box (DateTime.UtcNow.ToString "o") ]
        |> ignore

    /// Removes somebody from the list, and NOTHING ELSE.
    ///
    /// Deliberately leaves the pinned key in known_peers and every saved line in
    /// messages. Removing a friend is a statement about a list, not a request to
    /// forget a person: dropping the pin would mean the next card from them
    /// arrives as a first sighting and is accepted silently, which turns a
    /// tidy-up into a hole in trust on first use. Forgetting a conversation is a
    /// separate act and belongs to Chats.
    let remove (root: string) (local: Handle) (peer: Handle) =
        use db = Db.openAt (IdentityStore.databaseIn root)

        Db.executeWith
            db
            "DELETE FROM friends WHERE owner = $owner AND handle = $handle"
            [ "$owner", box local.Folded; "$handle", box peer.Folded ]
        |> ignore

    /// The list, ordered by the folded handle so it does not reshuffle when
    /// somebody signs in.
    let all (root: string) (local: Handle) =
        use db = Db.openAt (IdentityStore.databaseIn root)

        Db.query
            db
            "SELECT display FROM friends WHERE owner = $owner ORDER BY handle"
            [ "$owner", box local.Folded ]
            (fun r -> r.GetString 0)
        |> List.choose (Handle.TryParse >> Result.toOption)
        |> List.toArray

    let has (root: string) (local: Handle) (peer: Handle) =
        use db = Db.openAt (IdentityStore.databaseIn root)

        Db.query
            db
            "SELECT 1 FROM friends WHERE owner = $owner AND handle = $handle"
            [ "$owner", box local.Folded; "$handle", box peer.Folded ]
            (fun _ -> ())
        |> List.isEmpty
        |> not
