namespace EmuSen.Pegasus

open System
open System.IO
open Microsoft.Data.Sqlite

/// Opening and migrating the small SQLite databases the application keeps.
///
/// There are two kinds of storage in Pegasus and they are not the same thing.
/// A note is a CRDT and stays one: its data model is a Yjs document, because
/// two people editing the same sentence at once is a merge problem and no table
/// solves it. Everything else here is ordinary local bookkeeping — which
/// identities exist on this machine, which peer keys have been pinned — and
/// that is rows, with the constraints doing work that hand-rolled file parsing
/// was doing badly.
///
/// The clearest example is trust on first use. As text it was an append-only
/// file with a "first line for a handle wins" rule enforced by folding over the
/// lines in order, which is a constraint written as an algorithm. As a table it
/// is a PRIMARY KEY and INSERT OR IGNORE: the database refuses the second write
/// and there is nothing left to get wrong.
module Db =

    /// Bumped when the schema below changes in a way an existing file has to be
    /// migrated for. Stored in SQLite's own user_version pragma rather than a
    /// table of our own, because it costs nothing and is one less thing to
    /// create before it can be read.
    ///
    /// 2 is the first bump that is a real migration rather than another CREATE
    /// TABLE IF NOT EXISTS, and it is the case the comment on openAt below
    /// predicted: messaging added COLUMNS to two tables that already exist on
    /// somebody's disk, and a column cannot be added by re-running a create.
    [<Literal>]
    let SchemaVersion = 2

    let private schema =
        [ """
          CREATE TABLE IF NOT EXISTS identities (
              handle      TEXT PRIMARY KEY,
              display     TEXT NOT NULL,
              created     TEXT NOT NULL,
              public_key  BLOB NOT NULL,
              kdf         TEXT NOT NULL,
              iterations  INTEGER NOT NULL,
              salt        BLOB NOT NULL,
              secret      BLOB NOT NULL
          )
          """
          // owner is the folded handle of whoever is signed in, so signing in
          // under a second identity does not inherit the first one's contacts.
          // The composite key is what makes first-sighting-wins a property of
          // the table rather than a rule the reader has to enforce.
          """
          CREATE TABLE IF NOT EXISTS known_peers (
              owner       TEXT NOT NULL,
              handle      TEXT NOT NULL,
              fingerprint TEXT NOT NULL,
              public_key  BLOB NOT NULL,
              first_seen  TEXT NOT NULL,
              PRIMARY KEY (owner, handle)
          )
          """
          // Relay servers this identity has signed in to, so the address is
          // typed once rather than every launch. Owner-scoped for the same
          // reason known_peers is: two identities on a machine are two people.
          //
          // THE PASSPHRASE IS DELIBERATELY NOT A COLUMN HERE. There is nothing
          // to seal it under -- the password that unlocks the identity is not
          // retained past sign-in, and an ECDSA key cannot encrypt -- so
          // remembering it would mean a secret sitting in the clear in a file
          // whose whole point is that the secret in it is not. Retyping a
          // passphrase is a smaller cost than that. See Pegasus_Identity.md §8.
          """
          CREATE TABLE IF NOT EXISTS servers (
              owner     TEXT NOT NULL,
              host      TEXT NOT NULL,
              port      INTEGER NOT NULL,
              last_used TEXT NOT NULL,
              PRIMARY KEY (owner, host, port)
          )
          """
          // The saved buddy list, and it is NOT the roster. The roster is who
          // is signed in to a relay this second and empties when the connection
          // drops; this is who you decided to keep, and it survives being
          // offline, changing servers and reinstalling the program. Presence is
          // painted onto it by matching handles, which is the arrangement every
          // messenger since AIM has used and the reason a buddy list can show
          // somebody as offline at all — you cannot show the absence of
          // somebody you were not already listing.
          //
          // Owner-scoped like known_peers, because two identities on one
          // machine are two people and one's friends are not the other's.
          """
          CREATE TABLE IF NOT EXISTS friends (
              owner    TEXT NOT NULL,
              handle   TEXT NOT NULL,
              display  TEXT NOT NULL,
              added_at TEXT NOT NULL,
              PRIMARY KEY (owner, handle)
          )
          """
          // Saved conversations. Plaintext, deliberately, and the reason is
          // consistency rather than indifference: notes are already stored in
          // the clear in the workspace, so sealing transcripts here would
          // protect the shorter half of what this program keeps about you while
          // claiming to protect it all. What IS sealed is the key that opens
          // messages in transit (identities.message_secret), which is the thing
          // an attacker without your disk could otherwise use. Pegasus_Format.md
          // §7 states the on-disk position plainly so nobody infers a stronger
          // one from the word "encrypted" elsewhere in this project.
          //
          // PRIMARY KEY (owner, peer, id) IS THE DEDUPLICATION, and it is doing
          // real work rather than being tidy. Chariot redelivers anything it has
          // not seen an acknowledgement for, so a client that dies between
          // receiving a message and writing it down is MEANT to be handed that
          // message again — INSERT OR IGNORE turns the second copy into a
          // no-op instead of a second line in the transcript.
          //
          // Two clocks, on purpose. `sent_at` is the sender's word and is what
          // is shown; `received_at` is this machine's and is what orders the
          // list. A correspondent whose clock is an hour out therefore cannot
          // scramble a transcript, only mislabel their own lines.
          """
          CREATE TABLE IF NOT EXISTS messages (
              owner       TEXT NOT NULL,
              peer        TEXT NOT NULL,
              id          TEXT NOT NULL,
              sent_at     INTEGER NOT NULL,
              received_at TEXT NOT NULL,
              outbound    INTEGER NOT NULL,
              body        TEXT NOT NULL,
              PRIMARY KEY (owner, peer, id)
          )
          """
          """
          CREATE INDEX IF NOT EXISTS messages_conversation
              ON messages (owner, peer, received_at)
          """ ]

    /// Columns added to tables that already exist on somebody's disk.
    ///
    /// Kept apart from the schema above because the two are not the same kind
    /// of statement: everything up there is idempotent by construction, and
    /// ALTER TABLE ADD COLUMN is not — SQLite raises on a column that is
    /// already there. Guarding with a lookup rather than swallowing the error
    /// means a failure here is still a failure, instead of being indistinguish-
    /// able from the expected case.
    ///
    /// All four are nullable, which is what makes the migration safe to run
    /// against a store somebody has been using: an identity written before
    /// messaging existed has no messaging key, and NULL is the honest way to say
    /// so. IdentityStore.unlock is where one gets generated, because that is the
    /// only moment the password needed to seal it is in hand.
    let private additions =
        [ "identities", "message_public", "BLOB"
          "identities", "message_secret", "BLOB"
          // The messaging half of a pinned card. Beside the identity key rather
          // than in a table of its own, because they are pinned together and as
          // one decision: a card whose identity key is not this one is refused
          // outright, so storing them apart would invite checking one without
          // the other.
          "known_peers", "message_key", "BLOB" ]

    let private execute (connection: SqliteConnection) (sql: string) =
        use command = connection.CreateCommand()
        command.CommandText <- sql
        command.ExecuteNonQuery() |> ignore

    /// Whether a table already carries a column, from SQLite's own catalogue.
    ///
    /// PRAGMA table_info rather than a query against the column, because asking
    /// for a column that is not there is an error and "did it throw" is not a
    /// way to interrogate a schema — it cannot tell a missing column from a
    /// missing table or a locked file.
    let private hasColumn (connection: SqliteConnection) (table: string) (column: string) =
        use command = connection.CreateCommand()
        command.CommandText <- $"PRAGMA table_info({table})"
        use reader = command.ExecuteReader()
        let mutable found = false

        while reader.Read() do
            if reader.GetString 1 = column then found <- true

        found

    /// Opens the database, creating the file and the schema if they are absent.
    ///
    /// Creating and opening stay ONE code path for everything expressible as
    /// CREATE TABLE IF NOT EXISTS, which is still most of it, so there is no
    /// first-run branch to get wrong. The added columns run after those and are
    /// guarded individually, so a store at version 1 and a store created this
    /// second end up identical rather than nearly so — the second-commonest
    /// migration defect after not writing one at all.
    ///
    /// Journal mode is left at the default rather than switched to WAL. These
    /// databases are tiny and single-process, WAL would buy concurrency nothing
    /// here needs, and it would leave rows sitting in a -wal companion file,
    /// which makes "prove the sealed key never lands in storage unencrypted"
    /// awkward to check honestly.
    let openAt (path: string) =
        let directory = Path.GetDirectoryName path

        if not (String.IsNullOrEmpty directory) then
            Directory.CreateDirectory directory |> ignore

        let fresh = not (File.Exists path)
        let connection = new SqliteConnection($"Data Source={path}")
        connection.Open()

        for statement in schema do
            execute connection statement

        for table, column, kind in additions do
            if not (hasColumn connection table column) then
                execute connection $"ALTER TABLE {table} ADD COLUMN {column} {kind}"

        execute connection $"PRAGMA user_version = {SchemaVersion}"

        // Owner-only, and only worth setting when we are the one creating it:
        // a user who has deliberately widened the permissions should not have
        // that undone on every open. Windows has no equivalent and throws.
        if fresh && not (OperatingSystem.IsWindows()) then
            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

        connection

    /// Binds a value, mapping F# null-ish cases onto DBNull rather than letting
    /// the provider guess.
    let bind (command: SqliteCommand) (name: string) (value: obj) =
        command.Parameters.AddWithValue(name, if isNull value then box DBNull.Value else value)
        |> ignore

    let executeWith (connection: SqliteConnection) (sql: string) (parameters: (string * obj) list) =
        use command = connection.CreateCommand()
        command.CommandText <- sql
        for name, value in parameters do bind command name value
        command.ExecuteNonQuery()

    /// Runs a query and projects every row, so callers never hold a live reader
    /// and cannot leak one past the connection it came from.
    let query (connection: SqliteConnection) (sql: string) (parameters: (string * obj) list) (read: SqliteDataReader -> 'a) =
        use command = connection.CreateCommand()
        command.CommandText <- sql
        for name, value in parameters do bind command name value
        use reader = command.ExecuteReader()
        [ while reader.Read() do yield read reader ]
