module EmuSen.Pegasus.Tests.TrustTests

open System
open System.IO
open System.Text
open Xunit
open EmuSen.Pegasus

let private tempRoot () =
    let dir = Path.Combine(Path.GetTempPath(), "pegasus-trust", Guid.NewGuid().ToString "N")
    Directory.CreateDirectory dir |> ignore
    dir

// ---------------------------------------------------------------------------
// Attestation — proving you hold the key you claim
// ---------------------------------------------------------------------------

[<Fact>]
let ``a proof verifies against the key that made it`` () =
    use alice = Peers.identity "alice"
    let nonce = Crypto.newChallenge ()
    let proof = Attestation.prove alice nonce

    match Attestation.verify alice.PublicKey alice.Fingerprint nonce proof with
    | Ok() -> ()
    | Error why -> failwith why

[<Fact>]
let ``a proof made for one challenge does not answer another`` () =
    // What stops a proof recorded from an earlier session being replayed into
    // this one. The nonce is fresh per session for exactly this reason.
    use alice = Peers.identity "alice"
    let proof = Attestation.prove alice (Crypto.newChallenge ())
    let different = Crypto.newChallenge ()

    Assert.True(Attestation.verify alice.PublicKey alice.Fingerprint different proof |> Result.isError)

[<Fact>]
let ``one peer's proof does not pass as another's`` () =
    use alice = Peers.identity "alice"
    use bob = Peers.identity "bob"
    let nonce = Crypto.newChallenge ()
    let fromAlice = Attestation.prove alice nonce

    Assert.True(Attestation.verify bob.PublicKey bob.Fingerprint nonce fromAlice |> Result.isError)

[<Fact>]
let ``claiming an id that is not your key's fingerprint is refused`` () =
    // The attack the fingerprint check exists for, and the one it is easy to
    // leave open: an impostor presents ITS OWN key next to the id of the person
    // it is pretending to be, and signs the challenge perfectly, because it
    // does hold the key it sent. Verifying the signature alone would pass it.
    // What refuses it is insisting the claimed id be that key's fingerprint.
    use impostor = Peers.identity "impostor"
    use victim = Peers.identity "alice"
    let nonce = Crypto.newChallenge ()

    // Signed correctly, over the victim's id, with the impostor's own key.
    let forged = impostor.Sign(Attestation.payload nonce victim.Fingerprint)

    match Attestation.verify impostor.PublicKey victim.Fingerprint nonce forged with
    | Ok() -> failwith "an impostor passed by presenting its own key beside someone else's id"
    | Error why ->
        Assert.Contains(victim.Fingerprint.Value, why)
        Assert.Contains(impostor.Fingerprint.Value, why)

[<Fact>]
let ``a signature over the bare challenge is not a proof`` () =
    // Domain separation. If a peer can be induced to sign anything else in some
    // future exchange, that signature must not be presentable here as an
    // identity proof, so what is signed is never the raw challenge.
    use alice = Peers.identity "alice"
    let nonce = Crypto.newChallenge ()
    let bare = alice.Sign nonce

    Assert.True(Attestation.verify alice.PublicKey alice.Fingerprint nonce bare |> Result.isError)

[<Fact>]
let ``the payload commits to both the challenge and the signer`` () =
    use alice = Peers.identity "alice"
    use bob = Peers.identity "bob"
    let one = Crypto.newChallenge ()
    let two = Crypto.newChallenge ()

    Assert.NotEqual<byte[]>(Attestation.payload one alice.Fingerprint, Attestation.payload two alice.Fingerprint)
    Assert.NotEqual<byte[]>(Attestation.payload one alice.Fingerprint, Attestation.payload one bob.Fingerprint)

// ---------------------------------------------------------------------------
// KnownPeers — trust on first use
// ---------------------------------------------------------------------------

let private local = Handle.Parse "local"

[<Fact>]
let ``a peer seen for the first time is written down`` () =
    let root = tempRoot ()
    use alice = Peers.identity "alice"

    Assert.Equal(FirstSight, KnownPeers.trust root local alice.Peer alice.PublicKey)
    Assert.Equal<(string * PeerId)[]>([| "alice", alice.Fingerprint |], KnownPeers.pinnedFor root local)

[<Fact>]
let ``the same peer is recognised next time`` () =
    let root = tempRoot ()
    use alice = Peers.identity "alice"

    KnownPeers.trust root local alice.Peer alice.PublicKey |> ignore
    Assert.Equal(Recognised, KnownPeers.trust root local alice.Peer alice.PublicKey)

[<Fact>]
let ``a different key claiming a pinned handle is an impostor`` () =
    // The whole point of the table. A signature proves the far side holds the
    // key it sent; only the pin can say whether that key is the one that turned
    // up last time under this name.
    let root = tempRoot ()
    use alice = Peers.identity "alice"
    use impostor = Identity.Generate(Handle.Parse "alice")

    KnownPeers.trust root local alice.Peer alice.PublicKey |> ignore

    match KnownPeers.trust root local impostor.Peer impostor.PublicKey with
    | Impostor(pinned, offered) ->
        Assert.Equal(alice.Fingerprint, pinned)
        Assert.Equal(impostor.Fingerprint, offered)
    | other -> failwith $"expected an impostor, got {other}"

[<Fact>]
let ``a refused peer does not overwrite the pin`` () =
    // Being told "impostor" is only half of it. If the attempt had replaced the
    // stored key, the real peer would be refused ever afterwards and the
    // impostor would be the one recognised.
    let root = tempRoot ()
    use alice = Peers.identity "alice"
    use impostor = Identity.Generate(Handle.Parse "alice")

    KnownPeers.trust root local alice.Peer alice.PublicKey |> ignore
    KnownPeers.trust root local impostor.Peer impostor.PublicKey |> ignore

    Assert.Equal<(string * PeerId)[]>([| "alice", alice.Fingerprint |], KnownPeers.pinnedFor root local)
    Assert.Equal(Recognised, KnownPeers.trust root local alice.Peer alice.PublicKey)

[<Fact>]
let ``handles are pinned independently`` () =
    let root = tempRoot ()
    use alice = Peers.identity "alice"
    use bob = Peers.identity "bob"

    Assert.Equal(FirstSight, KnownPeers.trust root local alice.Peer alice.PublicKey)
    Assert.Equal(FirstSight, KnownPeers.trust root local bob.Peer bob.PublicKey)
    Assert.Equal(Recognised, KnownPeers.trust root local alice.Peer alice.PublicKey)

[<Fact>]
let ``two identities on one machine do not share a contact list`` () =
    // What the owner column is for. Signing in under a second handle must not
    // inherit the first one's pins, or one identity's trust decisions silently
    // become another's.
    let root = tempRoot ()
    let other = Handle.Parse "other"
    use alice = Peers.identity "alice"

    Assert.Equal(FirstSight, KnownPeers.trust root local alice.Peer alice.PublicKey)
    Assert.Equal(FirstSight, KnownPeers.trust root other alice.Peer alice.PublicKey)
    Assert.Empty(KnownPeers.pinnedFor root (Handle.Parse "nobody"))

[<Fact>]
let ``pinning is case-insensitive in the handle`` () =
    let root = tempRoot ()
    use alice = Peers.identity "alice"
    use shouting = Identity.Generate(Handle.Parse "ALICE")

    KnownPeers.trust root local alice.Peer alice.PublicKey |> ignore

    // A different key under the same name spelled differently is still the same
    // name, or the pin would be trivial to walk around.
    match KnownPeers.trust root local shouting.Peer shouting.PublicKey with
    | Impostor _ -> ()
    | other -> failwith $"expected an impostor, got {other}"

[<Fact>]
let ``the trust rule the application uses refuses a changed key, and says both`` () =
    let root = tempRoot ()
    let trust = Controller.pinnedTrust root local
    use alice = Peers.identity "alice"
    use impostor = Identity.Generate(Handle.Parse "alice")

    Assert.True(trust alice.Peer alice.PublicKey |> Result.isOk)
    Assert.True(trust alice.Peer alice.PublicKey |> Result.isOk)

    match trust impostor.Peer impostor.PublicKey with
    | Ok() -> failwith "a changed key was accepted"
    | Error why ->
        // Both fingerprints, because the person reading this has to decide
        // whether their peer reinstalled or whether this is not their peer.
        Assert.Contains(alice.Fingerprint.Value, why)
        Assert.Contains(impostor.Fingerprint.Value, why)
