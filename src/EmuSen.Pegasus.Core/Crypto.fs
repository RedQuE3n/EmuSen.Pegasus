namespace EmuSen.Pegasus

open System
open System.Security.Cryptography
open System.Text

/// Join codes and the pre-shared-key envelope. Threat model in Pegasus_Sync.md §5.
module Crypto =

    [<Literal>]
    let NonceBytes = 12

    [<Literal>]
    let TagBytes = 16

    [<Literal>]
    let KeyBytes = 32

    [<Literal>]
    let Iterations = 210_000

    [<Literal>]
    let SaltBytes = 16

    /// Fixed salt, and it is a real weakness rather than an oversight.
    ///
    /// Both peers have to arrive at the same key knowing only the join code,
    /// and at the moment they need it they have no channel over which to agree
    /// on a random salt -- the channel is the thing the key is for. So the salt
    /// cannot vary, and an attacker can precompute against the entire 9,216-code
    /// space that newJoinCode draws from. The iteration count raises the cost of
    /// doing that; it does not change the shape of the problem.
    ///
    /// This is a pre-shared key for two people on a LAN or a private network,
    /// not a security boundary. Anyone who wants the stronger property should
    /// run Pegasus over WireGuard or Tailscale rather than expect this layer to
    /// be more than it is. Full threat model, and what the encryption does and
    /// does not buy, in Pegasus_Sync.md §5.
    ///
    /// Contrast derivePassword below, where only one machine derives the key and
    /// the salt is therefore random.
    let private salt = Encoding.UTF8.GetBytes "pegasus/v1/join"

    /// Words chosen to be unambiguous when read aloud over a phone.
    let private words =
        [| "amber"; "anchor"; "banjo"; "beacon"; "cactus"; "candle"; "cobalt"; "comet"
           "dagger"; "domino"; "ember"; "falcon"; "garnet"; "harbor"; "indigo"; "jigsaw"
           "kettle"; "lantern"; "marble"; "nutmeg"; "orbit"; "pepper"; "quartz"; "ribbon"
           "saffron"; "tulip"; "umbra"; "velvet"; "walnut"; "yonder"; "zenith"; "zodiac" |]

    /// A code like "7-lantern-quartz". Entropy is stated in Pegasus_Sync.md §5.
    let newJoinCode () =
        let pick () = words[RandomNumberGenerator.GetInt32 words.Length]
        $"{RandomNumberGenerator.GetInt32(1, 10)}-{pick ()}-{pick ()}"

    let deriveKey (joinCode: string) =
        let normalised = joinCode.Trim().ToLowerInvariant()
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes normalised, salt, Iterations, HashAlgorithmName.SHA256, KeyBytes)

    let newSalt () = RandomNumberGenerator.GetBytes SaltBytes

    /// Derives a key from a password, for sealing an identity file.
    ///
    /// Same KDF as deriveKey above, with the one difference that matters: the
    /// salt is passed in and is random per identity, where the join code's is
    /// fixed. That is not an inconsistency between the two. A join code must
    /// produce the same key on two machines that have not spoken yet, so there
    /// is no opportunity to agree on a random salt and the weakness is forced.
    /// A password is only ever derived on the machine that stores the file, so
    /// the salt can be random, is stored beside the ciphertext, and
    /// precomputation is off the table.
    ///
    /// The iteration count is a parameter rather than the constant because the
    /// identity file records the count it was written with. Raising Iterations
    /// later must not lock anyone out of a file written under the old one.
    let derivePassword (password: string) (salt: byte[]) (iterations: int) =
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes password, salt, iterations, HashAlgorithmName.SHA256, KeyBytes)

    /// nonce (12) || ciphertext || tag (16). A fresh random nonce per frame, so
    /// no counter has to survive a reconnect.
    let seal (key: byte[]) (plaintext: byte[]) =
        let nonce = RandomNumberGenerator.GetBytes NonceBytes
        let cipher = Array.zeroCreate<byte> plaintext.Length
        let tag = Array.zeroCreate<byte> TagBytes
        use aes = new AesGcm(key, TagBytes)
        aes.Encrypt(nonce, plaintext, cipher, tag)
        Array.concat [ nonce; cipher; tag ]

    /// Opens a sealed blob, or None.
    ///
    /// None covers a wrong key, a truncated envelope and a tampered one alike:
    /// GCM cannot tell you which, only that the tag did not verify, and it would
    /// be a lie to pretend otherwise. Callers name their own failure -- the
    /// session says "wrong join code", the identity store says "wrong password"
    /// -- which is the whole reason this exists separately from openSealed
    /// below, whose message would be nonsense on the identity path.
    let tryOpenSealed (key: byte[]) (sealedBytes: byte[]) =
        if sealedBytes.Length < NonceBytes + TagBytes then
            None
        else
            let cipherLen = sealedBytes.Length - NonceBytes - TagBytes
            let nonce = sealedBytes[0 .. NonceBytes - 1]
            let cipher = sealedBytes[NonceBytes .. NonceBytes + cipherLen - 1]
            let tag = sealedBytes[NonceBytes + cipherLen ..]
            let plain = Array.zeroCreate<byte> cipherLen
            use aes = new AesGcm(key, TagBytes)

            try
                aes.Decrypt(nonce, cipher, tag, plain)
                Some plain
            with :? AuthenticationTagMismatchException ->
                None

    let openSealed (key: byte[]) (sealedBytes: byte[]) =
        if sealedBytes.Length < NonceBytes + TagBytes then
            raise (ProtocolError "sealed frame is too short to contain a nonce and tag")

        match tryOpenSealed key sealedBytes with
        | Some plain -> plain
        | None -> raise (ProtocolError "frame failed authentication: wrong join code, or the stream was tampered with")

    /// Proves both sides derived the same key without putting it on the wire.
    let respondToChallenge (key: byte[]) (challenge: byte[]) =
        use mac = new HMACSHA256(key)
        mac.ComputeHash challenge

    let newChallenge () = RandomNumberGenerator.GetBytes 32

    let verifyChallenge (key: byte[]) (challenge: byte[]) (response: byte[]) =
        CryptographicOperations.FixedTimeEquals(respondToChallenge key challenge, response)
