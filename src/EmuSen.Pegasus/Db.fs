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
    [<Literal>]
    let SchemaVersion = 1

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
          """ ]

    let private execute (connection: SqliteConnection) (sql: string) =
        use command = connection.CreateCommand()
        command.CommandText <- sql
        command.ExecuteNonQuery() |> ignore

    /// Opens the database, creating the file and the schema if they are absent.
    ///
    /// Migration is deliberately the simplest thing that can work: every
    /// statement is CREATE TABLE IF NOT EXISTS, so opening an existing file and
    /// creating a new one are the same code path and there is no first-run
    /// branch to get wrong. When a column has to change, this grows a real
    /// migration keyed on user_version and SchemaVersion goes up.
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
