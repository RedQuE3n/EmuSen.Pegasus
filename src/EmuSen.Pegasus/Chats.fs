namespace EmuSen.Pegasus

open System

/// One line of a saved conversation.
///
/// `Outbound` rather than a sender handle, because a transcript has exactly two
/// sides and the other one is the conversation it belongs to. Storing the sender
/// as a handle would make "me" a string that has to agree with the identity
/// currently signed in, and disagreeing quietly is how a transcript ends up
/// attributing your own lines to somebody else after a rename.
type Line =
    { Id: MessageId
      Outbound: bool
      /// The sender's clock. Shown, not trusted -- see Types.fs on the Message
      /// frame, and note that this is the sender's word even for a line you
      /// wrote, since it was your own clock that stamped it.
      SentAt: DateTimeOffset
      Body: string }

/// Saved conversations, on disk, in the clear.
///
/// **In the clear is a decision and it is stated plainly here rather than left
/// to be discovered.** Notes are already written to the workspace unencrypted,
/// so sealing transcripts would protect the shorter half of what this program
/// keeps about a person while leaving the longer half open — and it would do it
/// while requiring the identity password to be held in memory for the whole
/// session, which is a live secret traded for a partial protection. What is
/// sealed is the messaging private key, because that is what an attacker
/// *without* your disk would need. Anyone who has your disk has your notes
/// already. Pegasus_Format.md §7.
///
/// The thing this store is asked to get right is not secrecy but IDEMPOTENCE.
/// Chariot redelivers any message it has not seen acknowledged, so the same
/// message legitimately arrives twice whenever a client dies between reading a
/// delivery and writing it down. `record` is built to make that a no-op.
module Chats =

    /// Writes a line down, and says whether it was new.
    ///
    /// The bool is the whole interface to deduplication and callers depend on
    /// it: the window appends to the transcript only when this returns true, so
    /// a redelivery costs a database round trip and changes nothing on screen.
    /// The alternative — checking for the id first and then inserting — is the
    /// same race the rest of this store keeps declining to write, since a
    /// second delivery can arrive between the two statements.
    ///
    /// INSERT OR IGNORE reports rows written, which is exactly the answer.
    let record (root: string) (local: Handle) (peer: Handle) (line: Line) =
        use db = Db.openAt (IdentityStore.databaseIn root)

        let inserted =
            Db.executeWith
                db
                "INSERT OR IGNORE INTO messages (owner, peer, id, sent_at, received_at, outbound, body)
                 VALUES ($owner, $peer, $id, $sent, $received, $outbound, $body)"
                [ "$owner", box local.Folded
                  "$peer", box peer.Folded
                  "$id", box line.Id.Value
                  "$sent", box (line.SentAt.ToUnixTimeMilliseconds())
                  "$received", box (DateTime.UtcNow.ToString "o")
                  "$outbound", box (if line.Outbound then 1 else 0)
                  "$body", box line.Body ]

        inserted = 1

    /// A conversation, oldest first.
    ///
    /// Ordered by arrival on THIS machine rather than by the sender's clock, so
    /// a correspondent whose clock is wrong — or who lies about it — mislabels
    /// their own lines without being able to reorder yours around them. The id
    /// breaks ties, so two messages recorded in the same millisecond come back
    /// in a stable order rather than whichever order SQLite felt like.
    let conversation (root: string) (local: Handle) (peer: Handle) =
        use db = Db.openAt (IdentityStore.databaseIn root)

        Db.query
            db
            "SELECT id, sent_at, outbound, body FROM messages
             WHERE owner = $owner AND peer = $peer
             ORDER BY received_at, id"
            [ "$owner", box local.Folded; "$peer", box peer.Folded ]
            (fun r ->
                { Id = MessageId(r.GetString 0)
                  SentAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64 1)
                  Outbound = r.GetInt32 2 = 1
                  Body = r.GetString 3 })
        |> List.toArray

    /// Everybody this identity has a saved conversation with, most recent
    /// first. What a window offers when nothing is open yet.
    let correspondents (root: string) (local: Handle) =
        use db = Db.openAt (IdentityStore.databaseIn root)

        Db.query
            db
            "SELECT peer, MAX(received_at) AS latest FROM messages
             WHERE owner = $owner GROUP BY peer ORDER BY latest DESC"
            [ "$owner", box local.Folded ]
            (fun r -> r.GetString 0)
        |> List.choose (Handle.TryParse >> Result.toOption)
        |> List.toArray

    /// Deletes a saved conversation.
    ///
    /// Separate from removing a friend on purpose (see Friends.remove): one is
    /// a statement about a list and this is a statement about a record, and
    /// collapsing them would mean tidying a buddy list silently destroyed
    /// history the user never asked to lose.
    let forget (root: string) (local: Handle) (peer: Handle) =
        use db = Db.openAt (IdentityStore.databaseIn root)

        Db.executeWith
            db
            "DELETE FROM messages WHERE owner = $owner AND peer = $peer"
            [ "$owner", box local.Folded; "$peer", box peer.Folded ]
        |> ignore
