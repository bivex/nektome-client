# NektoMe Client

A cross-platform desktop client for the NektoMe anonymous chat service, built with
**C# / .NET 10**, **Avalonia 12**, **DDD** and a **hexagonal (ports & adapters)**
architecture. The wire protocols (text chat + voice roulette) were recovered from
the official Android APKs (`NektoMe_4.1.7_APKPure.xapk` and the companion
`com.nektome.chatruletka.voice` 1.7.1) via static analysis; see
[docs/protocol.md](docs/protocol.md).

## Status

- `dotnet build NektoMe.slnx` — clean (0 errors, 0 warnings)
- `dotnet test` — 62/62 passing (protocol codecs, chat + voice session orchestration, Ed25519 auth key, G.711 codec)

## Run

```bash
dotnet run --project src/NektoMe.Ui
```

The window has two tabs:

- **Text chat** — Connect / Disconnect, Find partner / Stop, Leave dialog, message
  input (Enter sends), auto read-receipts, captcha prompt (auto-solved when the
  server supplies the solution) and a raw event log for protocol debugging.
- **Voice roulette** — Connect to the audio signaling server, search filters
  (peer sex + age range), Find peer / Stop, mute / hang up / report trouble,
  online counters, reCAPTCHA prompt (opens the challenge page, paste the token),
  media-state and event logs. Remote audio plays through the platform backend
  (CoreAudio AudioQueue on macOS) and your microphone is captured the same way
  (8 kHz mono PCM → G.711), so the peer hears you. On first call macOS asks for
  microphone permission — grant it to the terminal the app runs from.

Tokens persist to `~/.nektome-client/token.txt`, the stable device identity to
`~/.nektome-client/device.json`, and the voice identity to
`~/.nektome-client/voice-user.json`. Delete them to start fresh.

## Architecture

```
                    ┌──────────────────────────────────────────────────┐
                    │                 NektoMe.Ui (Avalonia)            │
                    │   MainWindow.axaml ← MainViewModel (MVVM)        │
                    │   dispatcher-marshals ChatEvents → UI state      │
                    └───────────────────────┬──────────────────────────┘
                                            │ ChatSession (application service)
┌───────────────────────────────────────────┼──────────────────────────────────────────┐
│                     NektoMe.Application   │                                          │
│                                           ▼                                          │
│   Protocol/ (WireNames, WireModels, ProtocolCodec)   Services/ChatSession            │
│   Events/ (ChatEvent, ChatEventStream)               Services/AuthKeyService         │
│                                                                                      │
│   ── driven ports (left) ──                 ── driven ports (right) ──               │
│   IChatTransport                            ITokenStore                              │
│   IAuthKeyService / ISecurityKeySigner      IDeviceIdentityProvider                  │
│   IClock / IRandomIdGenerator                                                        │
└──────────┬─────────────────┬─────────────────────────┬──────────────────┬────────────┘
           │                 │                         │                  │
┌──────────▼────────┐ ┌──────▼─────────────┐ ┌─────────▼──────────┐ ┌─────▼──────────┐
│ NektoMe.Infrastructure                                             │ NektoMe.Domain │
│ SocketIoChatTransport (SocketIOClient 4, WebSocket, /android)      │  pure model    │
│ Ed25519AuthKeySigner (BouncyCastle)                                │  Dialog        │
│ FileTokenStore, FileDeviceIdentityProvider                         │  Message       │
│ SystemClock, RandomIdGenerator, AddNektoMeChat() composition root  │  ChatError …   │
└────────────────────────────────────────────────────────────────────┴────────────────┘
```

**Dependency rule:** Domain → nothing. Application → Domain only, and only via its own
port interfaces. Infrastructure → Application (implements ports). Ui → Application +
Infrastructure (composition root: `AddNektoMeChat()` on `IServiceCollection`).

### Layers

| Project | Contents |
|---|---|
| `NektoMe.Domain` | Pure model: `Dialog`, `Message`, `ChatError`, `BanStatus`, `AppConfig`, `Identifiers` (`UserId`, `DialogId`, `MessageId`). No dependencies. |
| `NektoMe.Application` | Ports (`Abstractions/`), the wire protocols (`Protocol/`: event names, DTOs, codecs), application services `ChatSession` (auth, search, dialog lifecycle, own-message detection, read receipts) and `VoiceChatSession` (voice signaling state machine), event streams (`Events/`). |
| `NektoMe.Infrastructure` | Adapters: Socket.IO transports (text + voice), Ed25519 signer, SIPSorcery WebRTC audio engine + managed G.711 codec, CoreAudio playback sink, file persistence, clock/random primitives, DI composition (`AddNektoMeChat` / `AddNektoMeVoice`). |
| `NektoMe.Ui` | Avalonia MVVM shell with two tabs. `MainViewModel` / `VoiceViewModel` subscribe to their session's event stream and post each event through `Dispatcher.UIThread`. |
| `NektoMe.Application.Tests` | 62 xUnit tests over the codecs, session logic, crypto and G.711, using fake transports/engines. |

### Key design decisions

- **The transport never parses payloads.** `IChatTransport` moves opaque
  `OutboundMessage`/`NoticeMessage` records; all JSON mapping lives in
  `ProtocolCodec`, so the whole protocol is testable without a socket.
- **Own-message detection** correlates the `randomId` generated for each outgoing
  message with the server echo, falling back to `senderId == own user id`.
- **Auth key** = lowercase-hex Ed25519 self-signature: `seed = SHA256(deviceId)`,
  `key = sign(seed, seed)`, with the literal `"null"` fallback used by the app.
- **engine.io v3 by default** (`EngineIO.V3`) because the Android reference client
  uses socket.io-client 1.x (socket.io v2 wire protocol). Switchable via
  `SocketIoTransportOptions.Engine`.
- **Server notices → typed `ChatEvent`s** on a thread-safe observable stream; the UI
  layer only marshals them onto the Avalonia dispatcher.
- **Voice media without a native media stack.** The WebRTC engine offers PCMU
  only, so a ~100-line managed G.711 codec covers every negotiated payload —
  no SIPSorcery media backend required. Remote PCM is played through CoreAudio
  (`IAudioSink` port; other platforms get a silent no-op sink), and optional
  WAV dumps capture calls for debugging (`VoiceOptions.RemoteAudioDumpPath`).
- **Voice signaling quirks preserved:** candidates that arrive before the remote
  SDP are buffered and flushed after it, SDP/candidates cross the wire as
  JSON-encoded strings (matching the Android app), and re-connects reuse the
  last `connectionId` with a one-shot `peerSuccess` flag.

## Rebuilding from the APK findings

Protocol facts (endpoints, action names, payloads, auth key derivation) were
recovered by static analysis of the Android APK and are documented in
[`docs/protocol.md`](docs/protocol.md). The analysis workspace with the
APK-derived endpoint report lives in `/Volumes/External/Code/apk_flash`
(`reports/nektome_4.1.7_endpoints.md`).
