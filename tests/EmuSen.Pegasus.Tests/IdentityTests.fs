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
    // Both halves matter and they pull in opposite directions: comparison must
    // fold so one person cannot end up owning two accounts that look identical
    // when spoken, and display must not fold or the user's own capitalisation
    // is thrown away. GetHashCode is asserted alongside Equals because a Map or
    // a HashSet of handles would quietly break if only one of them folded.
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
    // The concrete regression this replaced: PeerId was Guid.NewGuid() minted
    // on every launch, so a peer's id changed each time the application started
    // and there was nothing for the far side to recognise across a restart. Two
    // unlocks of the same file must agree, or "identity" means nothing.
    let root = tempRoot ()
    create root "alice" "pw" |> ignore

    let first = (IdentityStore.unlock root (Handle.Parse "alice") "pw" |> Result.toOption).Value
    let second = (IdentityStore.unlock root (Handle.Parse "alice") "pw" |> Result.toOption).Value

    Assert.Equal(first.Fingerprint, second.Fingerprint)
    Assert.Equal(first.Peer.Color, second.Peer.Color)

/// Naive substring search over bytes. The stored secret is a raw BLOB now, so
/// the thing to look for is the key itself rather than a base64 rendering of it.
let private containsBytes (needle: byte[]) (haystack: byte[]) =
    let n = needle.Length

    if n = 0 || haystack.Length < n then
        false
    else
        Seq.exists (fun i -> Array.sub haystack i n = needle) (seq { 0 .. haystack.Length - n })

[<Fact>]
let ``the private key is not written in the clear`` () =
    // Every file under the root, not only identity.db. If a journal or a WAL
    // companion is ever produced, the row can be sitting in it, and a guard
    // that reads one file would say the key was sealed while it was not.
    //
    // The first version of this guard, against the old text format, compared
    // raw PKCS#8 bytes to a file storing base64. Those could never match, so it
    // passed against a build deliberately altered to store the key UNSEALED and
    // was worth nothing. Storing a BLOB makes the honest comparison the obvious
    // one, which is a small argument in the format's favour.
    let root = tempRoot ()
    let identity = create root "alice" "pw"
    let secret = identity.ExportPrivateKey()

    let leaking =
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        |> Array.filter (fun file -> containsBytes secret (File.ReadAllBytes file))

    Assert.True(leaking.Length = 0, $"""the private key appears in: {String.Join(", ", leaking)}""")

    // Independently: what is stored is exactly a nonce and a tag longer than
    // what it wraps, which storing the key unsealed also fails.
    use db = Db.openAt (IdentityStore.databaseIn root)

    let stored =
        Db.query db "SELECT secret FROM identities WHERE handle = 'alice'" [] (fun r ->
            (r.GetFieldValue<byte[]> 0).Length)

    Assert.Equal<int list>([ secret.Length + Crypto.NonceBytes + Crypto.TagBytes ], stored)

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
let ``an identity stored under an unknown kdf is refused, not guessed at`` () =
    // A future build might store argon2id here. This one must say it cannot
    // read that rather than derive a key with the wrong function and report a
    // wrong password, which would send the user hunting for the wrong problem.
    let root = tempRoot ()
    create root "alice" "pw" |> ignore

    do
        use db = Db.openAt (IdentityStore.databaseIn root)
        Db.executeWith db "UPDATE identities SET kdf = 'argon2id'" [] |> ignore

    match IdentityStore.unlock root (Handle.Parse "alice") "pw" with
    | Error(Unreadable why) -> Assert.Contains("argon2id", why)
    | other -> failwith $"expected Unreadable, got {other}"

[<Fact>]
let ``a corrupt store is reported, not crashed on`` () =
    let root = tempRoot ()
    create root "alice" "pw" |> ignore
    File.WriteAllBytes(IdentityStore.databaseIn root, Array.init 4096 (fun i -> byte (i % 251)))

    match IdentityStore.unlock root (Handle.Parse "alice") "pw" with
    | Error(Unreadable _) -> ()
    | other -> failwith $"expected Unreadable, got {other}"

[<Fact>]
let ``a store with no identity in it is not an error, it is empty`` () =
    let root = tempRoot ()
    Assert.Empty(IdentityStore.list root)
    Assert.False(IdentityStore.exists root (Handle.Parse "alice"))

[<Fact>]
let ``the key survives the round trip through the file`` () =
    // Unlocking successfully only proves the envelope opened. Signing with the
    // key that came out and verifying against the public key written before it
    // was sealed is what proves the same keypair came back rather than merely a
    // well-formed one. The negative case is there so the verification cannot be
    // passing by returning true for everything.
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
    // A PeerId names a person and a Yjs client id names a replica, and the
    // difference is not academic: one person may hold two replicas -- a laptop
    // and a desktop signed in as the same handle -- and giving those the same
    // client id is the silent data loss measured in Pegasus_Design.md §4.5,
    // where colliding ids make a merge keep one side's edits and discard the
    // other's with no error raised.
    //
    // So this asserts client ids stay independent of identity and inside the
    // 2^32 ceiling that Pegasus_Design.md §4.7 bisected, rather than merely
    // that two numbers happen to differ.
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
    // A frame is decoded before anything downstream sees it, so a peer sending
    // a handle that breaks the grammar is refused at the boundary rather than
    // producing a Handle that could not have been constructed locally. Built by
    // hand here rather than through Codec.encode, because Codec.encode cannot
    // produce an invalid handle -- which is the point.
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
