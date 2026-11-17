# Release Notes — KeedParsecNative v0.1

## Highlights

- Real WebRTC media transport (SIPSorcery): SDP/ICE trickle, STUN/TURN,
  DTLS-SRTP, data channels, H.264 video + PCMU audio tracks.
- DXGI Desktop Duplication capture → OpenH264 encode → client decode →
  Live Session rendering.
- WASAPI loopback system audio → µ-law/PCMU → client playback.
- Remote keyboard/mouse/wheel input with release-on-disconnect.
- MQTT signaling with per-peer envelopes, join tokens, host approval,
  rate limiting.

## Verified on this machine

- `dotnet build -c Release`: clean, 0 warnings.
- `dotnet test -c Release`: 32 unit + 7 integration tests pass.
- OpenH264 encode→decode smoke test: 30 frames encoded (1 IDR keyframe),
  Annex-B output, decoded BGRA frames correct dimensions.
- Published `KeedParsecNative.App.exe` launches and stays running.
- In-process SIPSorcery loopback tests: SDP/ICE/data-channel connect.

## Not verified (needs external resources)

- Two-machine host↔client session over MQTT + real network.
- TURN relay path (no TURN server configured).
- WASAPI capture/playback audible output (code path implemented; not
  manually verified with speakers).
- SendInput injection from a remote client (requires second machine).

## Distribution

`publish/KeedParsecNative-win-x64.zip` — self-contained win-x64 build,
no .NET runtime required. Extract and run `KeedParsecNative.App.exe`.
Configure `appsettings.json` (next to the exe) or env vars for the MQTT
broker; TURN credentials via env/user-secrets only.
