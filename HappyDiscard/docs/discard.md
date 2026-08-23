# Discard Protocol Compliance

Protocol: Discard  
RFC: RFC 863  
Conventional port: 9

## Supported transports

- TCP
- UDP

## TCP behavior

- Accepts stream connections.
- Reads and discards received bytes.
- Sends no response data.
- Continues until client disconnects, timeout, byte limit, or server shutdown.
- Supports IPv4, IPv6, and optional dual-stack mode.

## UDP behavior

- Receives individual datagrams.
- Discards each datagram payload.
- Sends no response.
- Maintains no session state.
- Supports IPv4 and IPv6.
- Disabled by default unless explicitly enabled.

## Intentional limits/deviations

- Local/development examples use port 7009 instead of privileged port 9.
- TCP sessions have configurable timeout and byte limit.
- Oversized UDP datagrams may be dropped according to MaxUdpDatagramBytes.
- Discarded payload content is not published to telemetry.
- Production deployments may expose TCP only and leave UDP disabled.

## Manual verification

See the README local protocol testing section.