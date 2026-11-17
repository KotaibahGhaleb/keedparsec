# KeedParsecNative

A native Windows low-latency desktop streaming application (Parsec-like) built
with C# / .NET 8, WPF, SIPSorcery WebRTC, DXGI Desktop Duplication, OpenH264,
WASAPI, and Win32 SendInput. No Electron, no web wrappers.

## Solution layout

```
src/
  KeedParsecNative.App           WPF UI + SessionMediaPipeline (media wiring)
  KeedParsecNative.Core          Abstractions (capture/codec/audio/input) + state machine
  KeedParsecNative.Contracts     Signaling envelopes, streaming/input contracts
  KeedParsecNative.Infrastructure Configuration (appsettings.json + env vars + user secrets)
  KeedParsecNative.Signaling     MQTT signaling client + RoomSessionCoordinator
  KeedParsecNative.Streaming     SIPSorceryMediaTransport (WebRTC), H.264 codecs, pipes
  KeedParsecNative.Capture       DXGI Desktop Duplication, monitor enum, WASAPI audio
  KeedParsecNative.Input         Input packet codec, SendInput injector, XInput gamepad
tests/
  KeedParsecNative.UnitTests
  KeedParsecNative.IntegrationTests
publish/win-x64                  Self-contained Release build (win-x64)
publish/KeedParsecNative-win-x64.zip  Distribution archive
```

## Build & test

```bash
dotnet build KeedParsecNative.slnx -c Release
dotnet test  KeedParsecNative.slnx -c Release
```

Run from source:

```bash
dotnet run --project src/KeedParsecNative.App -c Release
```

Or run the published build: `publish/win-x64/KeedParsecNative.App.exe`
(self-contained — no .NET install required).

## How a session works

1. **Host** opens *Host a Session*, picks a monitor, clicks Start. A 6-char room
   code is shown — share it with the client.
2. **Client** opens *Join a Session*, enters the code + a display name, clicks
   Connect. If host approval is enabled the host must click Approve.
3. Once the WebRTC peer connection reports `connected`, the media pipeline
   starts automatically:
   - Host: DXGI duplication → OpenH264 encode → H.264 RTP track.
   - Client: RTP → OpenH264 decode → Live Session view (WriteableBitmap).
   - Audio: WASAPI loopback → G.711 µ-law → PCMU RTP track → client playback.
   - Input: client keyboard/mouse → reliable/unreliable data channels →
     host `SendInput` injection. All injected inputs are released on
     disconnect/failure.
4. The **Live Session** page shows remote video and captures input while the
   session state is `Connected` and the role is Client.

## Configuration

`src/KeedParsecNative.App/appsettings.json` holds non-secret defaults. Override
with environment variables (prefix `KEEDPARSEC_`, e.g.
`KEEDPARSEC_Signaling__BrokerUri=mqtts://broker:8883`) or user secrets.

TURN credentials must **never** be committed — supply them via user secrets or
env vars (`KEEDPARSEC_Turn__Servers__0__Urls`, `...__Username`,
`...__Credential`). STUN defaults to `stun:stun.l.google.com:19302`.

## External requirements

- **MQTT broker** for signaling (e.g. `mqtt://localhost:1883`, or `mqtts://`
  for TLS). Without a reachable broker, host/join cannot complete.
- **STUN** works out of the box via Google's public STUN for NAT traversal.
- **TURN** is required for symmetric NAT / restrictive firewalls — configure
  `Turn:Servers` in settings/secrets.
- Two Windows machines (or two app instances) are needed for a real
  host↔client session; loopback WebRTC is covered by integration tests.

## Implemented

- WPF UI: host/join/settings/status pages + Live Session view with video
  rendering and input capture.
- Explicit async session state machine with cancellation.
- MQTT signaling (MQTTnet): room topics, JSON envelopes, QoS 1, TLS support.
- Room coordinator: crypto-random room codes, HMAC one-time join tokens,
  host approval, per-peer SDP/ICE envelopes, join rate limiting,
  single-use tokens.
- Real WebRTC via SIPSorcery 10.x: RTCPeerConnection, SDP offer/answer,
  trickle ICE, STUN/TURN, DTLS-SRTP, reliable + unordered data channels,
  H.264 video track, PCMU audio track.
- DXGI Desktop Duplication capture (Vortice): real GPU frames → BGRA.
- H.264 via OpenH264 (H.264Sharp 1.6.0): software encode/decode verified
  end-to-end on this machine; Media Foundation path retained as an optional
  hardware attempt (`MfH264Codec`) but not relied upon.
- WASAPI loopback capture (NAudio) → 8kHz mono PCM16 → µ-law → PCMU RTP;
  client playback via WaveOutEvent.
- Remote input: keyboard/mouse/wheel/gamepad over data channels → SendInput /
  ViGEm injection; release-all-inputs on disconnect.
- Per-peer input permissions (Parsec-style): host grants Keyboard / Mouse /
  Gamepad per peer at approval time and toggles them live in the peer list.
  Enforcement is host-side (`InputPermissionPolicy`); revoking gamepad or
  keyboard releases that peer's virtual controller slot and resets held keys.
- Codec diagnostics factory reporting honest encoder availability.

## Known limitations

- **Gamepad**: full path implemented — client XInput capture
  (`WindowsGamepadSource`, 0.15 deadzone, delta-encoded axis/button events)
  → data channel → host `ViGEmGamepadInjector` (ViGEmBus virtual Xbox 360,
  per-peer slot). Requires ViGEmBus driver on the host; missing driver =
  safe no-op. Per-slot release on peer disconnect.
- **Hardware H.264**: `H264CodecFactory` now creates `AutoH264Encoder` /
  `AutoH264Decoder`, which attempt Media Foundation hardware MFTs first
  (`MFT_ENUM_FLAG_HARDWARE | ASYNCMFT` — NVENC/AMF/QSV, DXVA/D3D11VA) and fall
  back to OpenH264 automatically. `IsHardwareAccelerated` and `Name` reflect
  the backend actually in use. On the dev machine no usable hardware MFT
  could be negotiated, so OpenH264 software was selected — hardware path needs
  verification on a machine with a supported GPU/driver.
- **Stats overlay**: `GetStatsAsync` reports ICE/PC state honestly; detailed
  fps/bitrate/jitter metrics are not yet surfaced in the UI.
- **Reconnection**: ICE restart plumbing exists in SIPSorcery but automatic
  reconnect UX is not implemented.
- **Multi-peer**: host supports multiple peers — each peer gets its own
  RTCPeerConnection/transport; video/audio are encoded once and fanned out
  to all transports. Late joiners attach via `AddHostTransport`. Per-peer
  input slots are assigned by transport index (max 4 gamepad slots).
- Audio is mono 8kHz PCMU (telephone quality) — adequate for voice/system
  audio, not high-fidelity.
