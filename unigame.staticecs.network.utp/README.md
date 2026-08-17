# Static ECS Network Unity Transport

## Capabilities

- Adapts Unity Transport 2.6 to the exact-packet `INetworkTransport` contract.
- Keeps client/server driver ownership outside the transport-neutral protocol package.
- Maps reliable packets to fragmentation plus reliable-sequenced delivery and commands to unreliable-sequenced delivery.
- Copies received native data into bounded `NetworkBufferPool` leases.

## Usage

Create one `UnityTransportClientHost` or `UnityTransportServerHost`, call `Update` before the protocol receive systems and `Flush` after protocol send systems, and dispose the host at shutdown. Server endpoints returned by `TryAccept` are passed to `NetworkServer.AddConnection`.

## Configuration

`UnityTransportSettings.Default` uses port 7777, a 1400-byte unreliable packet limit, a 64 KiB reliable limit, and bounded receive queues. Application-level chunking above 64 KiB is intentionally not provided.
