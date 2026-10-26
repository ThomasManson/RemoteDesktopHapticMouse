# Remote Desktop Haptics Bridge — Design

Status: **Phases 0–2 implemented** · 2026-10-26

> **Decisions made during implementation (these override the text below):**
> - Target: classic `mstsc.exe`; remote is a normal Windows 11 26H2 PC (admin available).
> - The scope is window move, snap and resize haptics. PowerPoint and Source C are deferred.
> - **Both ends are C#/.NET 10.** This machine has MSVC but no Windows SDK, so the plugin is a .NET
>   in-proc COM server: `EnableComHosting` produces `RdHaptic.Client.comhost.dll`, which is registered
>   in HKCU. The agent is a self-contained single-file exe, so the remote PC needs no runtime.
> - The named-pipe CLI is `haptic.ps1` rather than a second exe, to avoid a second ~37 MB self-contained binary.
> - The client reopens the device lazily on write failure (2 s throttle) instead of using
>   `CM_Register_Notification`.
> - The device fills unused waveform slots with junk (it echoes durations). The client therefore accepts
>   only values inside the waveform list's declared logical range (≥ 0x1001).

## 1. Problem

The Surface Mouse 2nd Edition has a haptic actuator that Windows 11 drives when you
snap or resize windows, drag and drop, hit PowerPoint alignment guides, and so on. In an
RDP session none of that reaches the mouse:

1. **The remote OS has no haptic device.** It sees only the RDP virtual mouse. Windows
   sends haptics to "the input device that most recently delivered input", and the
   virtual mouse cannot vibrate, so the remote shell never generates a haptic request.
2. **RDP has no haptics channel.** Even if the remote machine produced a haptic request,
   the client has no way to receive it.

Remote Desktop has no built-in option for this. The bridge has to provide both a source
of haptic events on the remote machine and a way to deliver them to the local mouse.

## 2. Verified facts (local machine, probed 2026-10-26)

| Item | Finding |
|---|---|
| Device | `VID 045E / PID 0880`, BLE HID, firmware rev 0614 |
| Haptics collection | `COL06`: top-level `SimpleHapticsController` (page `0x0E`, usage `0x01`), separate from the mouse collection (`COL01`) |
| Access | The collection **opens read/write from a non-admin process** (`CreateFile` + `WriteFile`). No driver is needed on the client. |
| Feature report `0x42` | Waveform list and duration list (ordinals 3–52) |
| Output report `0x43` | 3 bytes: `[0x43, ordinal, intensity 0–4]`. The optional Repeat, Retrigger and Cutoff fields are not supported. |
| Trigger test | `WriteFile` and `HidD_SetOutputReport` both accepted Click and Success pulses (`ok=True`) |

Waveform table read from the device (read it at runtime; do not hardcode):

| Ordinal | Waveform | Usage | Duration |
|---|---|---|---|
| 3 | Hover | 0x1008 | 9 ms |
| 4 | Collide | 0x1012 | 26 ms |
| 5 | Align | 0x1013 | 9 ms |
| 6 | Step | 0x1014 | 11 ms |
| 7 | Grow | 0x1015 | 200 ms |
| 8 | Success | 0x1009 | 70 ms |
| 9 | Error | 0x100A | 197 ms |
| 10 | Click | 0x1003 | 11 ms |

## 3. Architecture

```
 REMOTE (RDP session host)                           LOCAL (your PC)
┌───────────────────────────────────────┐           ┌────────────────────────────────────────┐
│  HapticAgent.exe  (per-user, no admin)│           │  mstsc.exe / msrdc.exe                 │
│                                       │           │  ┌──────────────────────────────────┐  │
│  Event sources ──► Haptic Router ──┐  │           │  │ HapticBridge.dll (DVC plugin)    │  │
│   A. Shell heuristics (WinEvents)  │  │           │  │  IWTSPlugin / IWTSListener       │  │
│   B. Local API (named pipe + CLI)  │  │   RDP     │  │       │                          │  │
│   C. [spike] Virtual haptic mouse  │  │  Dynamic  │  │       ▼                          │  │
│                                    ▼  │  Virtual  │  │  WaveformMapper (usage→ordinal)  │  │
│              WTSVirtualChannelOpenEx ─┼──Channel──┼─►│       │                          │  │
│              ("RDHAPTIC", DYNAMIC)    │ "RDHAPTIC"│  │       ▼                          │  │
│                                       │           │  │  HidHapticDevice ── WriteFile ───┼──┼──► Surface Mouse
└───────────────────────────────────────┘           │  └──────────────────────────────────┘  │     COL06, report 0x43
                                                    └────────────────────────────────────────┘
```

### 3.1 Transport: RDP Dynamic Virtual Channel (DVC)

- The DVC travels inside the existing RDP connection, so it needs no extra ports or
  firewall rules and works through RD Gateway and NAT.
- **Server side:** `WTSVirtualChannelOpenEx(WTS_CURRENT_SESSION, "RDHAPTIC",
  WTS_CHANNEL_OPTION_DYNAMIC | WTS_CHANNEL_OPTION_DYNAMIC_PRI_REAL)`. This runs as an
  ordinary user-mode call inside the session and **needs no admin**. Setting priority to
  REAL keeps haptics from queuing behind bulk traffic.
- **Client side:** an in-process COM DLL that implements `IWTSPlugin`. It is registered
  per user under `HKCU\Software\Microsoft\Terminal Server Client\Default\AddIns\RDHaptic`
  (`Name = {CLSID}`) with the COM class in `HKCU\Software\Classes\CLSID`, so it also
  **needs no admin**.

### 3.2 Wire protocol (v1)

All fields are little-endian. Each message is one DVC write.

```
Header   : u8 version (=1) | u8 type | u16 seq
type 0x01 HELLO        S→C  u16 agentVersion
type 0x02 CAPS         C→S  u8 count | count × (u16 usage, u16 durationMs) | u8 intensityLevels
type 0x10 PLAY         S→C  u16 waveformUsage (e.g. 0x1013 Align) | u8 intensityPct (0–100)
type 0x11 STOP         S→C  —
type 0x20 PING/PONG    both u32 timestampMs   (latency measurement)
```

The protocol carries **waveform usages, not ordinals**. The client maps each usage to the
ordinal this device uses and scales intensity from 0–100 to the device's 0–4 range.
Another haptic mouse with a different waveform table then works without changes on the
remote side. If the device lacks a waveform, the client uses a fallback chain
(e.g. Align → Click → Step).

### 3.3 Client: `HapticBridge.dll` (C++/Win32, x64 plus ARM64)

| Component | Responsibility |
|---|---|
| `Plugin` | `IWTSPlugin::Initialize` registers the listener for `RDHAPTIC`. Each connection gets its own `IWTSVirtualChannelCallback`. |
| `HidHapticDevice` | Finds collections whose usage is `0x0E/0x01` through `SetupDi`/`CM_Get_Device_Interface_List`, opens them `GENERIC_WRITE`, and reads feature report `0x42`. Watches for disconnects and reconnects with `CM_Register_Notification`, since the BLE mouse sleeps. |
| `WaveformMapper` | Maps usage to ordinal, applies the fallback chain, and scales intensity. |
| `Player` | Runs a single worker thread with a bounded queue of 4 entries that drops the oldest when full. Rate-limits by waveform duration so bursts do not smear together. Writes asynchronously and never blocks the RDP callback thread. |
| `Config` | Settings in `HKCU\Software\RDHaptic`: master enable, intensity multiplier, per-waveform mute, log level. |

C++ is chosen because the DLL loads in-process into `mstsc.exe`/`msrdc.exe`. A native DLL
avoids pulling the CLR into the RDP client and avoids COM-interop registration problems.

### 3.4 Remote: `HapticAgent.exe` (C# .NET 8, self-contained single file)

The agent runs in the user's session and starts at logon from the `HKCU\...\Run` key. It
needs no admin. It keeps the DVC open, reconnects when the session reconnects (using
`WTSRegisterSessionNotification`), and routes events from three sources:

**Source A: shell heuristics (main value, no admin).** These replicate the Windows 11
haptic moments the remote shell would normally produce:

| Trigger (remote) | Detection | Waveform |
|---|---|---|
| Window snaps to an edge or zone | `EVENT_SYSTEM_MOVESIZEEND` + the final rect matches a monitor work-area half or quarter, or `IsZoomed` | Align |
| Snap preview appears while dragging | `EVENT_SYSTEM_MOVESIZESTART` → poll the cursor at screen edges and corners while dragging | Hover |
| Resize hits the minimum or maximum size | `WM_GETMINMAXINFO` cannot be observed cross-process, so: rect stops changing while the cursor keeps moving during a sizing loop | Collide |
| Snap Layouts flyout zone hover | UIA `ElementFromPoint` on the flyout (class `SnapLayoutsFlyout`) while it is open | Hover / Step |
| Drag-and-drop over a valid target | `EVENT_OBJECT_DRAGDROPPED` / `EVENT_OBJECT_DRAGCOMPLETE` (UIA drag events) | Step / Success |
| Optional: button or menu-item hover | UIA focus and hover events, off by default | Hover |

Every heuristic is a separate class that can be toggled. All of them run through one
**debounce** with a per-waveform cooldown, so the mouse does not buzz constantly.

**Source B: local API (developer hook).** A named pipe `\\.\pipe\RDHaptic` plus a
`haptic.exe play align` CLI. Scripts, AutoHotkey or your own tools on the remote machine
can then trigger feedback, for example Success when a build finishes or Error on a failing
test.

**Source C (spike, optional): capture the remote OS's own haptic requests.** Install a
virtual haptic mouse on the remote machine: a UMDF/VHF HID device that exposes the same
`SimpleHapticsController` collection. When Windows or an app such as PowerPoint calls
`InputHapticsManager`, the request becomes an output report on the virtual device, and the
driver forwards it to the agent and then over the DVC. This is the only route that makes
**PowerPoint alignment guides and third-party haptic apps** work. It has high risk and
cost (see §5), so it is a separate spike after v1 works.

## 4. Build phases

| Phase | Deliverable | Proves |
|---|---|---|
| **0. Local driver** ✅ (spiked today) | `hapticctl.exe` local CLI: list devices, dump the waveform table, play a waveform | HID path and report format |
| **1. Pipe** | DVC plugin plus a minimal agent with only the `haptic.exe` CLI. Measure round-trip latency with PING. | DVC loads in your client and is not blocked by policy; latency is acceptable |
| **2. Shell haptics** | Source A heuristics: snap, edge hover, Collide, drag-drop | The everyday feel |
| **3. Polish** | Tray icon on the agent (enable, intensity, per-trigger toggles), per-user installers (no admin) for both ends, reconnect hardening, logging | Daily-driver quality |
| **4. Spike** | Source C virtual haptic mouse: feasibility first, then build if viable | Full fidelity in PowerPoint and other apps |

Repository layout:

```
/docs/DESIGN.md
/client/HapticBridge/      C++ DVC plugin (CMake + MSVC)
/client/hapticctl/         C++ local test CLI (shares HidHapticDevice)
/agent/HapticAgent/        C# .NET 8 remote agent
/agent/haptic/             C# CLI for the named pipe
/shared/protocol.md        wire format (single source of truth)
/install/                  per-user install/uninstall scripts (client + agent)
```

## 5. Risks and unknowns

| # | Risk | Impact | Mitigation / how we find out |
|---|---|---|---|
| R1 | **Which RDP client?** `mstsc.exe` supports DVC plugins. Windows App / `msrdc.exe` (AVD, Windows 365) also loads them but registers them differently. A browser client cannot load them. | Blocks P1 | Confirm the client; P1 tests both mstsc and msrdc registration |
| R2 | **Corporate policy blocks custom DVCs** (Intune/GPO DVC allow-listing, or AV flagging an unsigned DLL loaded into mstsc) | Blocks the whole approach | P1 is a short, cheap go/no-go. Fallback: a reverse TCP socket from the agent if the network allows it, which is unlikely through a gateway. |
| R3 | **Latency.** The RDP round trip (typically 20–80 ms) delays the haptic. Snap and Align feel wrong after about 100 ms. | Feel | `DYNAMIC_PRI_REAL`; measure in P1. The local heuristics fire on the event, not on the redraw. |
| R4 | **Heuristics are approximations.** They will not match Windows' exact timing and will miss app-specific haptics (PowerPoint). | Fidelity | Tunable per trigger, off-switch; Source C closes the gap |
| R5 | **Source C feasibility.** It needs admin plus a signed driver on the remote machine. Test-signing is usually impossible under Secure Boot and corporate policy. Windows also routes haptics to the *last input device*, which is the RDP mouse, not the virtual device, so the routing may never pick it. | Phase 4 may be impossible | Spike on a personal VM with test-signing before investing |
| R6 | **Concurrent writers.** Windows' own local haptics (for local apps) and the bridge both write report `0x43`. | Minor | Last writer wins, which matches the design. Only the RDP session's events go through the bridge. |
| R7 | BLE sleep or reconnect leaves a stale handle | Missed pulses | PnP notifications plus reopen on `ERROR_DEVICE_NOT_CONNECTED` |

## 6. Rejected alternatives

- **RemoteFX USB redirection of the mouse.** The mouse is Bluetooth, not USB. Redirecting
  it would also remove it from local use.
- **API hooking (Detours) of `InputHapticsManager` in explorer and PowerPoint on the
  remote machine.** It is fragile across updates, likely to trip EDR/AV on a corporate
  machine, and needs injection into protected processes.
- **Calling `InputHapticsManager` on the client.** It targets "the device that last sent
  input to *this thread*". That rule is unreliable inside mstsc's input pipeline, and it
  hides the device's waveform table. Raw HID output reports are deterministic and already
  proven to work.
- **A separate TCP/WebSocket link.** It needs reachability from the remote machine back to
  the client, which NAT and RD Gateway normally prevent. A DVC avoids this.

## 7. Open questions for the owner

1. Which client do you use: `mstsc.exe`, Windows App / Remote Desktop (`msrdc`), or a
   browser? Is the target a plain RDP host, AVD, or a Windows 365 Cloud PC?
2. Can you run an unsigned per-user EXE on the remote machine (AppLocker/WDAC)? Do you
   have admin there (only relevant to Source C)?
3. Which Windows build is the remote machine on? (Affects UIA drag events and Snap Layouts
   class names.)
4. Which haptic moments matter most: window snapping, drag-drop, PowerPoint/Office
   alignment, or custom triggers?
