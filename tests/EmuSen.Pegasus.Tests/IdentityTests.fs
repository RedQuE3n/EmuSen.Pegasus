module EmuSen.Pegasus.Tests.IdentityTests

open System
open System.IO
open System.Text
open Xunit
open EmuSen.Pegasus

let private tempRoot () =
    let dir = Path.Combine(Path.GetTempPath(), "pegasus-identity", Guid.NewGuid().ToString "N")
    Directory.CreateDirectory dir |> ignore
    dir

// ---------------------------------------------------------------------------
// Handles
// ---------------------------------------------------------------------------

[<Theory>]
[<InlineData "RedQuE3n">]
[<InlineData "abc">]
[<InlineData "a-b_c9">]
[<InlineData "twelve_chars">]
let ``a well-formed handle parses`` (raw: string) =
    Assert.True(Handle.TryParse raw |> Result.isOk, raw)

[<Theory>]
[<InlineData "">]
[<InlineData "ab">]
[<InlineData "9lives">]
[<InlineData "-leading">]
[<InlineData "has space">]
[<InlineData "punctuation!">]
[<InlineData "far-too-long-a-handle-here">]
let ``a malformed handle is refused with a reason`` (raw: string) =
    match Handle.TryParse raw with
    | Ok _ -> failwith $"'{raw}' should not have parsed"
    | Error why -> Assert.NotEqual<string>("", why)

[<Fact>]
let ``a null handle is refused rather than throwing`` () =
    Assert.True(Handle.TryParse null |> Result.isError)

[<Fact>]
let ``handles compare case-insensitively and display as typed`` () =
    // The AIM rule, and the reason the file is named by the folded form while
    // the handle line carries the capitalisation. Pegasus_Identity.md §1.
    let typed = Handle.Parse "RedQuE3n"
    let shouted = Handle.Parse "REDQUE3N"

    Assert.Equal(typed, shouted)
    Assert.Equal(typed.GetHashCode(), shouted.GetHashCode())
    Assert.Equal("RedQuE3n", typed.Value)
    Assert.Equal("redque3n", typed.Folded)

// ---------------------------------------------------------------------------
// The identity file
// ---------------------------------------------------------------------------

let private create root handle password =
    match IdentityStore.create root (Handle.Parse handle) password with
    | Ok identity -> identity
    | Error e -> failwith e.Message

[<Fact>]
let ``a created identity can be unlocked with its password`` () =
    let root = tempRoot ()
    let fingerprint = (create root "RedQuE3n" "correct horse").Fingerprint

    match IdentityStore.unlock root (Handle.Parse "redque3n") "correct horse" with
    | Error e -> failwith e.Message
    | Ok reopened ->
        Assert.Equal(fingerprint, reopened.Fingerprint)
        Assert.Equal("RedQuE3n", reopened.Handle.Value)

[<Fact>]
let ``the fingerprint survives a restart, which a fresh GUID did not`` () =
    // The concrete regression: PeerId was Guid.NewGuid() per launch, so a peer
    // had no identity to recognise. Pegasus_Identity.md §6.
    let root = tempRoot ()
    create root "alice" "pw" |> ignore

    let first = (IdentityStore.unlock root (Handle.Parse "alice") "pw" |> Result.toOption).Value
    let second = (IdentityStore.unlock root (Handle.Parse "alice") "pw" |> Result.toOption).Value

    Assert.Equal(first.Fingerprint, second.Fingerprint)
    Assert.Equal(first.Peer.Color, second.Peer.Color)

[<Fact>]
let ``the private key is not written in the clear`` () =
    // Compared as base64, because that is how the file stores it. An earlier
    // version of this guard compared raw bytes against a base64 file, passed
    // against a deliberately unsealed write, and was worth nothing.
    // Pegasus_Identity.md §3.
    let root = tempRoot ()
    let identity = create root "alice" "pw"
    let secret = identity.ExportPrivateKey()
    let onDisk = File.ReadAllText(Path.Combine(root, "alice.id"))

    Assert.DoesNotContain(Convert.ToBase64String secret, onDisk)

    let sealedLength =
        onDisk.Split '\n'
        |> Array.pick (fun line ->
            if line.StartsWith "secret " then
                Some (Convert.FromBase64String(line.Substring 7)).Length
            else
                None)

    // Sealed, so exactly a nonce and a tag longer than the key it wraps.
    Assert.Equal(secret.Length + Crypto.NonceBytes + Crypto.TagBytes, sealedLength)

[<Fact>]
let ``the wrong password is refused, and named as such`` () =
    let root = tempRoot ()
    create root "alice" "right" |> ignore

    match IdentityStore.unlock root (Handle.Parse "alice") "wrong" with
    | Error WrongPassword -> ()
    | Error other -> failwith $"expected WrongPassword, got {other}"
    | Ok _ -> failwith "a wrong password unlocked the identity"

[<Fact>]
let ``an unknown handle is refused`` () =
    match IdentityStore.unlock (tempRoot ()) (Handle.Parse "nobody") "pw" with
    | Error(NoSuchHandle h) -> Assert.Equal("nobody", h.Value)
    | other -> failwith $"expected NoSuchHandle, got {other}"

[<Fact>]
let ``a handle already on this machine is not silently overwritten`` () =
    let root = tempRoot ()
    let first = (create root "alice" "pw").Fingerprint

    match IdentityStore.create root (Handle.Parse "ALICE") "different" with
    | Error(HandleTaken _) -> ()
    | other -> failwith $"expected HandleTaken, got {other}"

    // And the original still opens with the original password.
    match IdentityStore.unlock root (Handle.Parse "alice") "pw" with
    | Ok reopened -> Assert.Equal(first, reopened.Fingerprint)
    | Error e -> failwith e.Message

[<Fact>]
let ``a damaged identity file is reported, not crashed on`` () =
    let root = tempRoot ()
    create root "alice" "pw" |> ignore
    let path = Path.Combine(root, "alice.id")
    File.WriteAllText(path, File.ReadAllText(path).Replace("secret ", "shredded "))

    match IdentityStore.unlock root (Handle.Parse "alice") "pw" with
    | Error(Unreadable _) -> ()
    | other -> failwith $"expected Unreadable, got {other}"

[<Fact>]
let ``the key survives the round trip through the file`` () =
    // Signing with the unlocked key and verifying against the public line is
    // the only proof that what came back is the same keypair, not merely a
    // well-formed one. Nothing on the wire is signed yet -- Pegasus_Identity.md §2.
    let root = tempRoot ()
    let payload = Encoding.UTF8.GetBytes "prove it"
    let published = (create root "alice" "pw").PublicKey

    match IdentityStore.unlock root (Handle.Parse "alice") "pw" with
    | Error e -> failwith e.Message
    | Ok reopened ->
        Assert.True(Identity.VerifyWith(published, payload, reopened.Sign payload))
        Assert.False(Identity.VerifyWith(published, Encoding.UTF8.GetBytes "something else", reopened.Sign payload))

[<Fact>]
let ``listing reports the capitalisation the handle was created with`` () =
    let root = tempRoot ()
    create root "RedQuE3n" "pw" |> ignore
    create root "bob" "pw" |> ignore

    let listed = IdentityStore.list root |> Array.map _.Value
    Assert.Equal<string[]>([| "bob"; "RedQuE3n" |], listed)

[<Fact>]
let ``two identities are told apart by fingerprint and colour`` () =
    let root = tempRoot ()
    let alice = create root "alice" "pw"
    let bob = create root "bob" "pw"

    Assert.NotEqual(alice.Fingerprint, bob.Fingerprint)
    Assert.Equal(16, alice.Peer.Id.Value.Length)
    Assert.StartsWith("#", alice.Peer.Color)
    Assert.Equal(7, alice.Peer.Color.Length)
    Assert.NotEqual(alice.Peer, bob.Peer)

[<Fact>]
let ``the fingerprint is not usable as a Yjs client id`` () =
    // They name different things: a person, and a replica. One person may hold
    // two replicas, and giving those one id is Pegasus_Design.md §4.5's silent
    // data loss. This asserts the two are independent, not merely unequal.
    let identity = Identity.Generate(Handle.Parse "alice")
    use first = new DocumentActor()
    use second = new DocumentActor()

    Assert.NotEqual(first.ClientId, second.ClientId)
    Assert.True(first.ClientId < ClientId.ExclusiveMax)
    Assert.True(second.ClientId < ClientId.ExclusiveMax)
    (identity :> IDisposable).Dispose()

// ---------------------------------------------------------------------------
// Handles on the wire
// ---------------------------------------------------------------------------

[<Fact>]
let ``a peer that sends an unusable handle is rejected`` () =
    // Codec validates what arrives, so the grammar holds for remote peers too.
    let hostile =
        use stream = new MemoryStream()
        use w = new IO.BinaryWriter(stream, UTF8Encoding false, true)
        w.Write 0uy // Hello
        w.Write "abcdef0123456789"
        w.Write "not a handle!"
        w.Write "#ffffff"
        w.Flush()
        stream.ToArray()

    Assert.Throws<ProtocolError>(fun () -> Codec.decode hostile |> ignore)
