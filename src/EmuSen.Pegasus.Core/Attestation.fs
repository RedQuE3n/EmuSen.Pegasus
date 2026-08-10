namespace EmuSen.Pegasus

open System.Text

/// Proving that the peer claiming a fingerprint holds the key behind it.
///
/// The shape is a mutual challenge and response: each side sends a random nonce
/// and signs the nonce the other sent. There is no first-to-be-trusted end, and
/// neither side has to be the host, which keeps this working unchanged when a
/// relay is the thing on the far side rather than a person.
///
/// This is where `Pegasus_Identity.md` §2's hazard is closed. Both Chariot and
/// the desktop application need exactly this check, which is why it is in the
/// core rather than written twice.
module Attestation =

    /// Prefixed to everything signed here, so a signature made for this purpose
    /// cannot be replayed as a signature for some other purpose later. If a
    /// second thing in this project ever asks a peer to sign something, it gets
    /// its own tag and neither can be mistaken for the other. The version is in
    /// the tag so a future scheme cannot be confused with this one either.
    [<Literal>]
    let private Domain = "pegasus/identity-proof/v1"

    /// What actually gets signed: the tag, the verifier's nonce, and the
    /// signer's own fingerprint.
    ///
    /// The nonce makes each proof good for one session only. The fingerprint
    /// binds the signature to the identity being claimed, so a signature
    /// harvested from one peer cannot be presented by another peer claiming a
    /// different handle over the same nonce.
    let payload (nonce: byte[]) (signer: PeerId) =
        Array.concat
            [ Encoding.UTF8.GetBytes Domain
              nonce
              Encoding.UTF8.GetBytes signer.Value ]

    let prove (identity: Identity) (nonce: byte[]) =
        identity.Sign(payload nonce identity.Fingerprint)

    /// Checks a proof, and refuses for a stated reason rather than returning a
    /// bare false, because the reasons are different and a user seeing one of
    /// them should not be told the other.
    ///
    /// Two things are checked and the first is the one people forget. A peer
    /// may send any public key and claim any PeerId beside it; unless the id is
    /// verified to BE that key's fingerprint, an impostor can present the real
    /// person's id alongside its own key and sign perfectly.
    let verify (publicKey: byte[]) (claimed: PeerId) (nonce: byte[]) (signature: byte[]) =
        let actual = Fingerprint.ofPublicKey publicKey

        if actual <> claimed then
            Error $"peer claims id {claimed.Value} but its key fingerprints to {actual.Value}"
        elif not (Identity.VerifyWith(publicKey, payload nonce claimed, signature)) then
            Error "peer did not sign the challenge with the key it presented"
        else
            Ok()
