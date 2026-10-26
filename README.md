# RdHaptic — haptic mouse feedback over Remote Desktop

Makes a haptic mouse (built for the Surface Mouse 2nd Edition) pulse for window move, snap and resize
events that happen **inside an RDP session**. See [docs/DESIGN.md](docs/DESIGN.md) for how it works.

```
remote PC: RdHapticAgent.exe ──RDHAPTIC dynamic virtual channel──► local PC: mstsc.exe + plugin ──HID──► mouse
```

| Event on the remote desktop | Pulse |
|---|---|
| Dragged window reaches a snap edge, corner or the top | Hover (light) |
| Window released into a snap zone, or snapped via the Snap Layouts bar | Align (sharp) |
| Resize hits the window's minimum/maximum size | Collide (soft) |
| Cursor moves onto a zone of the Snap Layouts bar (drag to top centre) | Hover |
| Dragged window crosses monitors | Step |

## Build

Requires the .NET 10 SDK. Run `.\build.ps1`. It runs the tests and fills `dist\client` and `dist\agent`.

## Install

**Local PC** (the one with the mouse, where you run the classic Remote Desktop client):

```powershell
dist\client\install-client.ps1      # per user, no admin; needs the .NET 10 x64 runtime
```

Then close and reopen Remote Desktop.

**Remote PC:** copy `dist\agent` into the session (for example, paste it through RDP), then run:

```powershell
.\install-agent.ps1                  # per user, no admin; self-contained, no .NET needed
```

The agent starts at every logon. Check the link from the remote side:

```powershell
& "$env:LOCALAPPDATA\RdHaptic\agent\haptic.ps1" status   # ok connected (8 waveforms)
& "$env:LOCALAPPDATA\RdHaptic\agent\haptic.ps1" ping     # ok 23 ms
& "$env:LOCALAPPDATA\RdHaptic\agent\haptic.ps1" play success
```

`haptic.ps1 play <hover|collide|align|step|grow|success|error|click> [0-100]` also works from your own
scripts, for example to pulse when a build finishes.

## Tuning

**Remote agent:** `%APPDATA%\RdHaptic\agent.json`. Changes apply as soon as you save; no restart needed. Settings added in newer versions appear in the file automatically, but existing values are kept.

| Key | Default | Meaning |
|---|---|---|
| `Intensity` | 100 | Base intensity, 0–100 |
| `EdgeHover` / `SnapAlign` / `ResizeCollide` / `SnapBarHover` / `MonitorCross` | all on | Turn each trigger on or off |
| `EdgePx` | 2 | How close to a screen edge counts as touching it. Raise it if you use a windowed (not full-screen) RDP session and the cursor escapes the window before reaching the edge. |
| `CornerPx` | 40 | Size of the corner regions that snap a window to a quarter of the screen |
| `CollidePx` | 12 | How far the cursor must outrun a window edge before a resize counts as hitting its limit |
| `CollideStallMs` | 250 | How long a dragged edge must stay still before it counts as hitting a limit. Raise it if a slow app still pulses mid-resize; lower it for a quicker pulse. |
| `CooldownMs` | 120 | Minimum gap between two pulses of the same waveform |
| `Waveforms` | `{}` | Overrides for which waveform a trigger plays, e.g. `{"TopEdgeEnter": "Align"}`. Triggers and their defaults: `EdgeEnter` (side edges and corners, Hover), `TopEdgeEnter` (top edge and vertical maximize, Collide), `SnapRelease` (Align), `SnapBarZone` (Hover), `ResizeLimit` (Collide), `MonitorCross` (Step). Use `hapticctl.exe play <name>` locally to compare them. |
| `WaveformIntensity` | `{}` | Per-waveform scaling in percent, e.g. `{"Hover": 60}`. The mouse has 4 levels, so steps are coarse. |

**Local client:** optional values under `HKCU\Software\RdHaptic`. `Enabled` (DWORD 0/1), `IntensityScale`
(DWORD 0–100), `Muted` (string, e.g. `hover,collide`).

To try the triggers without RDP, run `RdHapticAgent.exe --local --console` on the PC with the mouse. It plays
pulses directly on the local mouse and prints each event as it happens.

## Diagnostics

| Command / file | Purpose |
|---|---|
| `dist\client\hapticctl.exe list` | Show the mouse's haptic waveform table |
| `hapticctl.exe play align` / `demo` | Fire pulses directly over HID |
| `hapticctl.exe selftest` | Run the plugin through a simulated RDP channel |
| `hapticctl.exe comtest` | Check that the COM registration activates |
| `%LOCALAPPDATA%\RdHaptic\client.log` | Plugin log: load, channel open, CAPS |
| `%LOCALAPPDATA%\RdHaptic\agent.log` | Agent log: channel state, latency, each drag and pulse |

## Uninstall

Run `uninstall-client.ps1` on the local PC and `uninstall-agent.ps1` on the remote PC.
