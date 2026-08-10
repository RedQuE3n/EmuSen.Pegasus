namespace EmuSen.Pegasus

open System
open System.Buffers.Binary
open System.IO
open System.Threading

/// Frames on a stream. Layout in Pegasus_Sync.md §3.
///
///     int32   length of everything that follows
///     bytes   envelope, in the clear
///     bytes   sealed payload
///
/// There are two ways to use this and the difference is the whole point of the
/// envelope. A peer holds the key, so it reads and writes whole frames. A relay
/// holds no key, so it reads the envelope and moves the sealed payload around
/// without ever being able to open it. Both use the same wire; they differ only
/// in whether they can decrypt what they are carrying.
///
/// This lives in the core rather than beside Session because Chariot needs the
/// relay half and the desktop application needs the peer half, and one
/// implementation of a wire format is the reason the core exists at all.
module Framing =

    let private readExactly (stream: Stream) (count: int) (ct: CancellationToken) =
        task {
            let buffer = Array.zeroCreate<byte> count
            let mutable read = 0

            while read < count do
                let! n = stream.ReadAsync(Memory(buffer, read, count - read), ct)

                if n = 0 then
                    raise (EndOfStreamException "peer closed the connection")

                read <- read + n

            return buffer
        }

    /// Writes an already-sealed payload. This is a relay's only way to send:
    /// it forwards bytes it received and cannot inspect.
    let writeSealed (stream: Stream) (envelope: Envelope) (sealedBytes: byte[]) (ct: CancellationToken) =
        task {
            let head = Codec.encodeEnvelope envelope
            let prefix = Array.zeroCreate<byte> 4
            BinaryPrimitives.WriteInt32LittleEndian(Span prefix, head.Length + sealedBytes.Length)
            do! stream.WriteAsync(ReadOnlyMemory prefix, ct)
            do! stream.WriteAsync(ReadOnlyMemory head, ct)
            do! stream.WriteAsync(ReadOnlyMemory sealedBytes, ct)
            do! stream.FlushAsync ct
        }

    /// Reads the destination and the payload without opening it.
    ///
    /// The length is checked before anything is allocated, so a hostile or
    /// corrupt prefix cannot make us reserve an arbitrary buffer before we have
    /// had a chance to reject it. That check matters more here than on the peer
    /// path: a relay talks to strangers by definition.
    let readSealed (stream: Stream) (ct: CancellationToken) =
        task {
            let! prefix = readExactly stream 4 ct
            let length = BinaryPrimitives.ReadInt32LittleEndian(ReadOnlySpan prefix)

            if length <= 0 || length > Codec.MaxFrameBytes then
                raise (ProtocolError $"frame length {length} is out of range")

            let! buffer = readExactly stream length ct
            let envelope, consumed = Codec.decodeEnvelope buffer

            if consumed >= buffer.Length then
                raise (ProtocolError "frame carries an envelope and no payload")

            return envelope, buffer[consumed..]
        }

    let writeFrame (stream: Stream) (key: byte[]) (envelope: Envelope) (frame: Frame) (ct: CancellationToken) =
        writeSealed stream envelope (Crypto.seal key (Codec.encode frame)) ct

    let readFrame (stream: Stream) (key: byte[]) (ct: CancellationToken) =
        task {
            let! envelope, sealedBytes = readSealed stream ct
            return envelope, Codec.decode (Crypto.openSealed key sealedBytes)
        }
