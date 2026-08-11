module EmuSen.Pegasus.Tests.Peers

open System.Collections.Concurrent
open EmuSen.Pegasus
open EmuSen.Pegasus.Controller

/// A throwaway identity with a real keypair, for tests that need a peer but do
/// not care about sign-in.
///
/// It generates rather than faking a PeerInfo by hand, so the id and colour a
/// test sees are derived the same way the application derives them. A test that
/// built `{ Id = PeerId "abc"; ... }` would still pass if fingerprinting broke.
///
/// `use` disposes the ECDsa handle on the way out; Peer has already been
/// materialised by then and holds no reference to the key.
let named (handle: string) =
    use identity = Identity.Generate(Handle.Parse handle)
    identity.Peer

/// A live identity the caller owns, for tests that need to sign rather than
/// just be named. Not disposed here, unlike `named` above -- the caller holds it.
let identity (handle: string) = Identity.Generate(Handle.Parse handle)

/// The trust rule for tests that are not about trust: take whoever turns up.
///
/// Deliberately not the default anywhere in the application. A session refusing
/// to decide who it trusts, and making the caller say, is what lets this exist
/// without weakening what ships.
let acceptAny: PeerInfo -> byte[] -> Result<unit, string> = fun _ _ -> Ok()

/// A card directory for tests that are not about cards: believe every card and
/// remember what it said.
///
/// It DOES still verify the signature, unlike `acceptAny` beside it, and the
/// difference is deliberate. Skipping the signature would make a message that
/// opens prove nothing about who sent it, which is the property most of these
/// tests are implicitly leaning on; what this drops is the PIN, which is the
/// part that needs a store on disk. So: internally consistent cards are taken,
/// and no claim is made about whether that identity is the one you meant.
type CardBook() =
    let cards = ConcurrentDictionary<string, byte[]>()

    member _.Accept: Card -> Result<Card, string> =
        fun card ->
            if Messaging.verifyCard card then
                cards[card.Handle.Folded] <- card.Messaging
                Ok card
            else
                Error $"{card.Handle.Value} sent a messaging key its identity key did not sign"

    member _.KeyFor: Handle -> byte[] option =
        fun handle ->
            match cards.TryGetValue handle.Folded with
            | true, key -> Some key
            | _ -> None

/// Contacts held in memory, for tests that need a controller rather than a
/// store. Nothing here touches the disk of whoever is running the suite.
///
/// A fresh one per call, because two tests sharing a buddy list would pass or
/// fail depending on the order they ran in.
let contacts () : Contacts =
    let book = CardBook()
    let friends = ResizeArray<Handle>()
    let saved = ConcurrentDictionary<string, ResizeArray<Line>>()

    let conversationOf (peer: Handle) =
        saved.GetOrAdd(peer.Folded, (fun _ -> ResizeArray()))

    { Trust = acceptAny
      AcceptCard = book.Accept
      MessagingKey = book.KeyFor
      Friends = fun () -> friends.ToArray()
      AddFriend =
        fun handle ->
            if not (friends |> Seq.exists (fun f -> f.Folded = handle.Folded)) then
                friends.Add handle
      RemoveFriend = fun handle -> friends.RemoveAll(fun f -> f.Folded = handle.Folded) |> ignore
      Record =
        fun peer line ->
            // The same idempotence the real store gets from a primary key. A
            // double called in a test that exercises redelivery has to behave
            // like the thing it stands in for, or the test proves nothing.
            let lines = conversationOf peer

            lock lines (fun () ->
                if lines |> Seq.exists (fun existing -> existing.Id = line.Id) then
                    false
                else
                    lines.Add line
                    true)
      Conversation = fun peer -> let lines = conversationOf peer in lock lines (fun () -> lines.ToArray()) }
