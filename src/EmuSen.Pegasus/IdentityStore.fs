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
        | Unreadable why -> $"the identity file could not be read: {why}"

/// Identity files on disk, one per handle.
///
/// The format is line-oriented text, `key value` per line:
///
///     pegasus-identity 1
///     handle RedQuE3n
///     created 2026-08-09T23:41:07Z
///     public <base64 SubjectPublicKeyInfo>
///     kdf pbkdf2-sha256 210000 <base64 salt>
///     secret <base64 nonce || ciphertext || tag>
///
/// Text rather than a binary blob on purpose: somebody should be able to answer
/// "is my key actually encrypted in there" with `cat` and no debugger. Every
/// line is public except `secret`, which is the PKCS#8 private key sealed under
/// the password-derived key.
///
/// This is deliberately not the .pegasus format (Pegasus_Format.md). An identity
/// is written once and read many times, so it needs no append log, no compaction
/// and no torn-write recovery -- there is no stream of updates to recover.
module IdentityStore =

    [<Literal>]
    let FileVersion = 1

    [<Literal>]
    let private Kdf = "pbkdf2-sha256"

    /// Beside the workspace, not inside it. An identity is not a note, and the
    /// workspace is deliberately NOT partitioned by handle: notes stay where
    /// they have always been and every identity on a machine sees the same
    /// ones. A handle says who you are to your peer; it is not a separate
    /// account of files. Partitioning would strand every note written before
    /// sign-in existed, for no benefit to two people who each own their machine.
    let defaultRoot =
        let data =
            match Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData with
            | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".local", "share")
            | path -> path

        Path.Combine(data, "Pegasus", "identity")

    /// Named by the folded handle, so `RedQuE3n` and `redque3n` cannot become
    /// two accounts. The capitalisation to display lives in the file's own
    /// handle line.
    let private pathOf (root: string) (handle: Handle) =
        Path.Combine(root, handle.Folded + ".id")

    /// Split on the FIRST space only, so a value may contain spaces -- the kdf
    /// line does. Lines that do not split are ignored rather than rejected,
    /// which lets a future version add lines this build has never heard of.
    let private fields (text: string) =
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun line ->
            match line.Trim().Split(' ', 2) with
            | [| key; value |] -> Some(key, value)
            | _ -> None)
        |> Map.ofArray

    /// Every identity on this machine, for the sign-in window to offer.
    ///
    /// Reads each file for its handle line so the list shows the capitalisation
    /// the handle was created with. A file too damaged to name itself falls back
    /// to its filename rather than vanishing from the list -- an identity you
    /// cannot open should still be visible, or the user is left wondering where
    /// it went.
    let list (root: string) =
        if not (Directory.Exists root) then
            [||]
        else
            Directory.GetFiles(root, "*.id")
            |> Array.sortBy Path.GetFileName
            |> Array.choose (fun path ->
                let named =
                    try
                        Map.tryFind "handle" (fields (File.ReadAllText path))
                    with _ ->
                        None

                named
                |> Option.defaultValue (Path.GetFileNameWithoutExtension path)
                |> Handle.TryParse
                |> Result.toOption)

    let exists (root: string) (handle: Handle) = File.Exists(pathOf root handle)

    /// Seals the private key under the password and writes the file.
    ///
    /// The salt is RANDOM here and stored beside the ciphertext, which is the
    /// opposite of the join code's fixed salt in Crypto.deriveKey. That is not
    /// an inconsistency: there, both peers must arrive at the same key from the
    /// code alone with no round trip in which to agree on a salt, so the salt
    /// cannot vary and precomputation against the small code space is possible.
    /// Here only this machine derives the key, from a password only its owner
    /// types, so nothing forces the weakness and taking it anyway would be
    /// carelessness. Same primitive, opposite constraint.
    ///
    /// The iteration count is written into the file rather than assumed, so
    /// raising it later does not lock anyone out of an existing identity.
    ///
    /// The exported key is zeroed once sealed. It cannot be un-exported, but it
    /// can at least stop sitting in a heap block waiting for the collector.
    let private write (root: string) (identity: Identity) (password: string) =
        let salt = Crypto.newSalt ()
        let key = Crypto.derivePassword password salt Crypto.Iterations
        let pkcs8 = identity.ExportPrivateKey()
        let secret = Crypto.seal key pkcs8
        CryptographicOperations.ZeroMemory(Span pkcs8)

        let created = DateTime.UtcNow.ToString "o"

        let text =
            String.concat "\n"
                [ $"pegasus-identity {FileVersion}"
                  $"handle {identity.Handle.Value}"
                  $"created {created}"
                  $"public {Convert.ToBase64String identity.PublicKey}"
                  $"kdf {Kdf} {Crypto.Iterations} {Convert.ToBase64String salt}"
                  $"secret {Convert.ToBase64String secret}"
                  "" ]

        Directory.CreateDirectory root |> ignore
        let path = pathOf root identity.Handle
        File.WriteAllText(path, text)

        // Owner-only where the platform has the concept. Windows does not, and
        // SetUnixFileMode throws there rather than no-opping.
        if not (OperatingSystem.IsWindows()) then
            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

    /// Generates a new keypair and writes it out. Refuses rather than
    /// overwriting: silently replacing an identity would destroy the only copy
    /// of a key, and the user who typed a handle they had forgotten they owned
    /// would never know.
    let create (root: string) (handle: Handle) (password: string) =
        if exists root handle then
            Error(HandleTaken handle)
        else
            let identity = Identity.Generate handle

            try
                write root identity password
                Ok identity
            with e ->
                // Nothing was stored, so the key is of no use to anyone; dispose
                // it rather than leaving a live handle to a key nobody holds.
                (identity :> IDisposable).Dispose()
                Error(Unreadable e.Message)

    /// Reads the file and opens the sealed key with the password.
    ///
    /// A wrong password is indistinguishable from a tampered file at this layer
    /// -- both are a GCM authentication failure -- and it is reported as
    /// WrongPassword because that is what it nearly always is. Note this uses
    /// Crypto.tryOpenSealed rather than openSealed: the raising form's message
    /// talks about join codes, which would be nonsense on this path.
    let unlock (root: string) (handle: Handle) (password: string) =
        let path = pathOf root handle

        if not (File.Exists path) then
            Error(NoSuchHandle handle)
        else
            try
                let f = fields (File.ReadAllText path)

                match Map.tryFind "pegasus-identity" f, Map.tryFind "kdf" f, Map.tryFind "secret" f with
                | Some version, _, _ when version.Trim() <> string FileVersion ->
                    Error(Unreadable $"version {version.Trim()}, this build understands {FileVersion}")
                | Some _, Some kdf, Some secret ->
                    match kdf.Split(' ', StringSplitOptions.RemoveEmptyEntries) with
                    | [| named; iterations; salt |] when named = Kdf ->
                        let derived = Crypto.derivePassword password (Convert.FromBase64String salt) (int iterations)

                        match Crypto.tryOpenSealed derived (Convert.FromBase64String secret) with
                        | None -> Error WrongPassword
                        | Some pkcs8 ->
                            // Prefer the file's handle line to the one that was
                            // typed, so signing in as "redque3n" still displays
                            // "RedQuE3n". Falls back to what was typed if that
                            // line is missing or no longer parses.
                            let named =
                                Map.tryFind "handle" f
                                |> Option.bind (Handle.TryParse >> Result.toOption)
                                |> Option.defaultValue handle

                            let identity = Identity.OfPrivateKey(named, pkcs8)
                            CryptographicOperations.ZeroMemory(Span pkcs8)
                            Ok identity
                    | _ -> Error(Unreadable $"unrecognised kdf line '{kdf}'")
                | _ -> Error(Unreadable "a required line is missing")
            with e ->
                // Anything the file can throw -- bad base64, a truncated line, a
                // key that will not import -- is the file being unreadable. The
                // user can act on that; a stack trace they cannot.
                Error(Unreadable e.Message)
