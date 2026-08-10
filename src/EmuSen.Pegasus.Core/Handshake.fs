namespace EmuSen.Pegasus

open System
open System.IO
open System.Threading

/// Proves both ends derived the same key from the join code, without sending it.
///
/// This is a different question from the one Attestation answers and both are
/// asked. The join code decides whether a stranger may open a session at all;
/// the signature decides who they are once they have. Passing this proves
/// somebody read the code aloud to you, and nothing more -- an impostor who was
/// in the room when you did will get past it, and be refused by the proof.
module Handshake =

    let private challengeLength = 32

    let asHost (stream: Stream) (key: byte[]) (ct: CancellationToken) =
        task {
            let challenge = Crypto.newChallenge ()
            do! stream.WriteAsync(ReadOnlyMemory challenge, ct)
            do! stream.FlushAsync ct
            let response = Array.zeroCreate<byte> 32
            let mutable read = 0

            while read < 32 do
                let! n = stream.ReadAsync(Memory(response, read, 32 - read), ct)
                if n = 0 then raise (ProtocolError "peer closed during handshake")
                read <- read + n

            if not (Crypto.verifyChallenge key challenge response) then
                raise (ProtocolError "join code did not match")
        }

    let asJoiner (stream: Stream) (key: byte[]) (ct: CancellationToken) =
        task {
            let challenge = Array.zeroCreate<byte> challengeLength
            let mutable read = 0

            while read < challengeLength do
                let! n = stream.ReadAsync(Memory(challenge, read, challengeLength - read), ct)
                if n = 0 then raise (ProtocolError "peer closed during handshake")
                read <- read + n

            let response = Crypto.respondToChallenge key challenge
            do! stream.WriteAsync(ReadOnlyMemory response, ct)
            do! stream.FlushAsync ct
        }
