namespace EmuSen.Pegasus

open System
open System.IO
open System.Security.Cryptography

/// Every way signing in can refuse. These are returned rather than thrown
/// because none of them is exceptional -- typing the wrong password is an
/// ordinary thing a user does, and the sign-in window wants to put the reason
/// on screen, not catch something.
///
/// Message is what the window shows. Keep it in the user's vocabulary: they
/// know what a handle and a password are, and they do not know what PKCS#8 is.
type IdentityError =
    | InvalidHandle of why: string
    | HandleTaken of Handle
    | NoSuchHandle of Handle
    | WrongPassword
    | Unreadable of why: string

    member this.Message =
        match this with
        | InvalidHandle why -> why
        | HandleTaken h -> $"{h.Value} is already in use on this machine"
        | NoSuchHandle h -> $"no identity named {h.Value} on this machine"
        | WrongPassword -> "wrong password"
        | Unreadable why -> $"the identity store could not be read: {why}"

/// Identities on this machine, as rows in identity.db.
///
/// The `identities` table holds everything public about a keypair plus the
/// private half sealed under the password (Pegasus_Identity.md §4 carries the
/// KDF argument, which the move to SQLite did not change). The handle column is
/// the folded form and is the primary key, so "RedQuE3n" and "redque3n" cannot
/// become two accounts by construction rather than by a check somebody has to
/// remember to write; `display` carries the capitalisation to show.
///
/// This was one text file per handle until it was not. The property that was
/// given up is worth naming: you could `cat` the file and see for yourself that
/// the key was encrypted, with no tool but the one everybody has. The answer is
/// now one tool further away —
///
///     sqlite3 identity.db 'select handle, hex(secret) from identities'
///
/// — and the guard that the private key never reaches storage in the clear
/// moved across with it and still fails when the sealing is removed.
module IdentityStore =

    [<Literal>]
    let private Kdf = "pbkdf2-sha256"

    /// The directory the store lives in, beside the workspace rather than
    /// inside it: an identity is not a note, and the workspace is deliberately
    /// NOT partitioned by handle -- every identity on a machine sees the same
    /// notes. A handle says who you are to your peer; it is not a separate
    /// account of files, and partitioning would strand every note written
    /// before sign-in existed.
    let defaultRoot =
        let data =
            match Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData with
            | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".local", "share")
            | path -> path

        Path.Combine(data, "Pegasus", "identity")

    /// Callers pass the directory rather than the file, so that identities and
    /// pinned peer keys can share one database without every caller having to
    /// know they do.
    let databaseIn (root: string) = Path.Combine(root, "identity.db")

    let private read (reader: Microsoft.Data.Sqlite.SqliteDataReader) =
        {| Display = reader.GetString 0
           Kdf = reader.GetString 1
           Iterations = reader.GetInt32 2
           Salt = reader.GetFieldValue<byte[]> 3
           Secret = reader.GetFieldValue<byte[]> 4 |}

    /// Display handles of every identity on this machine, for the sign-in
    /// window to offer. Ordered by the folded handle so the list is stable.
    let list (root: string) =
        try
            use db = Db.openAt (databaseIn root)

            Db.query db "SELECT display FROM identities ORDER BY handle" [] (fun r -> r.GetString 0)
            |> List.choose (Handle.TryParse >> Result.toOption)
            |> List.toArray
        with _ ->
            // A store too damaged to list is a store with no identities to
            // offer. The sign-in window still works; creating one will report
            // the real error.
            [||]

    let exists (root: string) (handle: Handle) =
        use db = Db.openAt (databaseIn root)

        Db.query db "SELECT 1 FROM identities WHERE handle = $h" [ "$h", box handle.Folded ] (fun _ -> ())
        |> List.isEmpty
        |> not

    /// Generates a keypair and stores it sealed under the password.
    ///
    /// INSERT rather than INSERT OR REPLACE, and the primary key does the
    /// refusing: silently replacing an identity would destroy the only copy of
    /// a private key, and the user who typed a handle they had forgotten they
    /// owned would never find out.
    ///
    /// The salt is random per identity and stored beside the ciphertext, which
    /// is the opposite of the join code's fixed salt in Crypto.deriveKey. Only
    /// this machine derives this key, from a password only its owner types, so
    /// nothing forces the weakness there and taking it anyway would be
    /// carelessness. The iteration count is stored rather than assumed, so
    /// raising it later cannot lock anyone out of an identity made under the
    /// old one.
    let create (root: string) (handle: Handle) (password: string) =
        if exists root handle then
            Error(HandleTaken handle)
        else
            let identity = Identity.Generate handle

            try
                let salt = Crypto.newSalt ()
                let key = Crypto.derivePassword password salt Crypto.Iterations
                let pkcs8 = identity.ExportPrivateKey()
                let secret = Crypto.seal key pkcs8

                // It cannot be un-exported, but it can stop sitting in a heap
                // block waiting for the collector.
                CryptographicOperations.ZeroMemory(Span pkcs8)

                use db = Db.openAt (databaseIn root)

                Db.executeWith
                    db
                    "INSERT INTO identities (handle, display, created, public_key, kdf, iterations, salt, secret)
                     VALUES ($handle, $display, $created, $public, $kdf, $iterations, $salt, $secret)"
                    [ "$handle", box handle.Folded
                      "$display", box handle.Value
                      "$created", box (DateTime.UtcNow.ToString "o")
                      "$public", box identity.PublicKey
                      "$kdf", box Kdf
                      "$iterations", box Crypto.Iterations
                      "$salt", box salt
                      "$secret", box secret ]
                |> ignore

                Ok identity
            with e ->
                // Nothing was stored, so the key is of no use to anyone.
                (identity :> IDisposable).Dispose()
                Error(Unreadable e.Message)

    /// Opens the sealed key with the password.
    ///
    /// A wrong password and a tampered row are indistinguishable here -- both
    /// are a GCM authentication failure, and it would be a lie to claim
    /// otherwise -- so it is reported as WrongPassword, which is what it nearly
    /// always is. This uses Crypto.tryOpenSealed rather than openSealed because
    /// the raising form's message talks about join codes, which would be
    /// nonsense on this path.
    let unlock (root: string) (handle: Handle) (password: string) =
        try
            use db = Db.openAt (databaseIn root)

            let rows =
                Db.query
                    db
                    "SELECT display, kdf, iterations, salt, secret FROM identities WHERE handle = $h"
                    [ "$h", box handle.Folded ]
                    read

            match rows with
            | [] -> Error(NoSuchHandle handle)
            | row :: _ when row.Kdf <> Kdf -> Error(Unreadable $"unrecognised kdf '{row.Kdf}'")
            | row :: _ ->
                let derived = Crypto.derivePassword password row.Salt row.Iterations

                match Crypto.tryOpenSealed derived row.Secret with
                | None -> Error WrongPassword
                | Some pkcs8 ->
                    // The stored display form wins over the one that was typed,
                    // so signing in as "redque3n" still shows "RedQuE3n".
                    let named =
                        Handle.TryParse row.Display |> Result.defaultValue handle

                    let identity = Identity.OfPrivateKey(named, pkcs8)
                    CryptographicOperations.ZeroMemory(Span pkcs8)
                    Ok identity
        with e ->
            // Anything the store can throw -- a missing table, a column that
            // will not convert, a key that will not import -- is the store
            // being unreadable. The user can act on that; a stack trace no.
            Error(Unreadable e.Message)
