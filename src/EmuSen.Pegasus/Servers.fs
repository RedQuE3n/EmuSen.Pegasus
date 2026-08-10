namespace EmuSen.Pegasus

open System

/// A relay server this machine has signed in to, as the window needs it.
type ServerAddress =
    { Host: string
      Port: int }

    /// What a user reads and types: "chariot.example:9040". Parsing and
    /// printing live together so the two cannot drift apart.
    override this.ToString() = $"{this.Host}:{this.Port}"

/// What the window is allowed to know about remembered servers: the last one,
/// and how to record a new one. Two functions rather than a database path,
/// because a control that takes a path can only be tested with a database, and
/// this one wants to be testable with `Servers.forgetful`.
type ServerBook =
    { Recent: unit -> ServerAddress option
      Remember: ServerAddress -> unit }

/// Where this identity signs in, remembered so it is typed once.
///
/// Small, and worth having anyway: a relay's address is the one piece of the
/// pairing ritual that does NOT change between sessions — that is the entire
/// point of a relay — so retyping it every launch would give back most of what
/// the relay was for. Chariot_Design.md §9 in the EmuSen.Chariot repository is
/// the pass this belongs to.
///
/// The passphrase is not stored, and Db.fs says why beside the table: there is
/// nothing honest to seal it under. So this remembers where, never how to get
/// in — which is also why losing this database costs a user nothing they cannot
/// retype.
module Servers =

    /// Records a successful sign-in, or refreshes one already known.
    ///
    /// Called only after the connection succeeded. Remembering an address that
    /// was typed but did not work would fill the list with somebody's
    /// misspellings and then offer them back.
    let remember (root: string) (owner: Handle) (server: ServerAddress) =
        use db = Db.openAt (IdentityStore.databaseIn root)

        Db.executeWith
            db
            "INSERT INTO servers (owner, host, port, last_used) VALUES ($owner, $host, $port, $used)
             ON CONFLICT (owner, host, port) DO UPDATE SET last_used = $used"
            [ "$owner", box owner.Folded
              "$host", box server.Host
              "$port", box server.Port
              "$used", box (DateTime.UtcNow.ToString "o") ]
        |> ignore

    /// Most recently used first, so the head of the list is what to prefill.
    let recent (root: string) (owner: Handle) =
        try
            use db = Db.openAt (IdentityStore.databaseIn root)

            Db.query
                db
                "SELECT host, port FROM servers WHERE owner = $owner ORDER BY last_used DESC"
                [ "$owner", box owner.Folded ]
                (fun r -> { Host = r.GetString 0; Port = r.GetInt32 1 })
            |> List.toArray
        with _ ->
            // A store too damaged to read is a store with no addresses to
            // offer. Typing one still works, and signing in will report the
            // real error rather than this one.
            [||]

    let mostRecent (root: string) (owner: Handle) = recent root owner |> Array.tryHead

    /// The book one identity keeps on one machine.
    let bookFor (root: string) (owner: Handle) =
        { Recent = fun () -> mostRecent root owner
          Remember = remember root owner }

    /// A book that remembers nothing, for a window that has no business
    /// writing to somebody's database — the headless suite, and any caller that
    /// wants the control without the storage.
    let forgetful =
        { Recent = fun () -> None
          Remember = ignore }
