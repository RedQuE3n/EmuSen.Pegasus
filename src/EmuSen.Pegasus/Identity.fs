namespace EmuSen.Pegasus

open System
open System.IO
open System.Security.Cryptography

/// Everything sign-in can refuse to do. See Pegasus_Identity.md §3.
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

/// Fingerprint and caret colour are both derived from the public key, and
/// neither is the Yjs client id. See Pegasus_Identity.md §6.
module Fingerprint =

    /// Indexed rather than computed in a colour space, so no identity can land
    /// on an unreadable tint. Pegasus_Identity.md §6.
    let private palette =
        [| "#7c5cff"; "#2fa4a0"; "#d2691e"; "#4a90d9"
           "#c04a7a"; "#5aa02c"; "#b58900"; "#8f6fd0"
           "#e0533d"; "#2e8b8b"; "#a0522d"; "#3b7dd8" |]

    let ofPublicKey (publicKey: byte[]) =
        let digest = SHA256.HashData publicKey
        PeerId(Convert.ToHexStringLower digest[0..7])

    let colourOf (publicKey: byte[]) =
        let digest = SHA256.HashData publicKey
        palette[int digest[8] % palette.Length]

/// One signed-in person: a handle bound to a keypair this machine holds. The
/// keypair is not yet used on the wire -- Pegasus_Identity.md §2.
type Identity private (handle: Handle, key: ECDsa) =
    let publicKey = key.ExportSubjectPublicKeyInfo()

    member _.Handle = handle
    member _.PublicKey = publicKey
    member _.Fingerprint = Fingerprint.ofPublicKey publicKey

    member this.Peer : PeerInfo =
        { Id = this.Fingerprint
          Handle = handle
          Color = Fingerprint.colourOf publicKey }

    member _.Sign(data: byte[]) = key.SignData(data, HashAlgorithmName.SHA256)

    member _.Verify(data: byte[], signature: byte[]) =
        key.VerifyData(data, signature, HashAlgorithmName.SHA256)

    /// P-256 because .NET ships no Ed25519 -- Pegasus_Identity.md §5.
    static member Generate(handle: Handle) =
        new Identity(handle, ECDsa.Create ECCurve.NamedCurves.nistP256)

    static member internal OfPrivateKey(handle: Handle, pkcs8: byte[]) =
        let key = ECDsa.Create()
        let mutable read = 0
        key.ImportPkcs8PrivateKey(ReadOnlySpan pkcs8, &read)
        new Identity(handle, key)

    member internal _.ExportPrivateKey() = key.ExportPkcs8PrivateKey()

    /// Verifies a signature against a bare public key, with no private half.
    static member VerifyWith(publicKey: byte[], data: byte[], signature: byte[]) =
        use key = ECDsa.Create()
        let mutable read = 0
        key.ImportSubjectPublicKeyInfo(ReadOnlySpan publicKey, &read)
        key.VerifyData(data, signature, HashAlgorithmName.SHA256)

    interface IDisposable with
        member _.Dispose() = key.Dispose()

/// Identity files on disk, one per handle. Layout in Pegasus_Identity.md §3.
module IdentityStore =

    [<Literal>]
    let FileVersion = 1

    [<Literal>]
    let private Kdf = "pbkdf2-sha256"

    /// Beside the workspace rather than inside it: an identity is not a note,
    /// and the workspace is not partitioned by handle -- Pegasus_Identity.md §7.
    let defaultRoot =
        let data =
            match Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData with
            | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".local", "share")
            | path -> path

        Path.Combine(data, "Pegasus", "identity")

    let private pathOf (root: string) (handle: Handle) =
        Path.Combine(root, handle.Folded + ".id")

    let private fields (text: string) =
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun line ->
            match line.Trim().Split(' ', 2) with
            | [| key; value |] -> Some(key, value)
            | _ -> None)
        |> Map.ofArray

    /// Display handles of every identity on this machine, in folded order. A
    /// file too damaged to name itself falls back to its filename.
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

    /// Writes the identity out sealed under the password. The private key is
    /// zeroed once sealed rather than left for the collector.
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

        if not (OperatingSystem.IsWindows()) then
            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

    let create (root: string) (handle: Handle) (password: string) =
        if exists root handle then
            Error(HandleTaken handle)
        else
            let identity = Identity.Generate handle

            try
                write root identity password
                Ok identity
            with e ->
                (identity :> IDisposable).Dispose()
                Error(Unreadable e.Message)

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
                            // The file's own handle line carries the capitalisation to
                            // display; the filename is folded -- Pegasus_Identity.md §1.
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
                Error(Unreadable e.Message)
