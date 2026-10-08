<div align="center">

<img src="web/public/icon.png" width="96" alt="TPConsole icon">

# TPConsole

**See and shape every sound path of your TOPPING E2x2 OTG.**

A modern control app for Windows — inputs, mixes, outputs, loopbacks and your apps, all on one routing graph.

[![Latest release](https://img.shields.io/github/v/release/gkfyddl2662/TPConsole?label=download&color=e0a24e)](https://github.com/gkfyddl2662/TPConsole/releases/latest)
![Windows 10/11 x64](https://img.shields.io/badge/Windows-10%20%7C%2011%20x64-3f72b8)
![Device](https://img.shields.io/badge/device-TOPPING%20E2x2%20OTG-7cc4a4)

English · [한국어](README.ko.md)

<img src="docs/routing.png" alt="TPConsole routing view" width="100%">

</div>

## Why TPConsole

The E2x2 OTG can route a lot more than its front panel shows: four hardware mixes, three loopbacks,
four playback pairs. TPConsole puts all of it on one screen and connects it to what Windows is doing,
so you can tell at a glance where your mic, your game and your music are going — and change it by
dragging a wire.

## Features

**Routing graph**
- Every input, Windows playback pair, MIX A–D, output and loopback as a node; connections as wires
- Send levels and pan per mix, live meters on wires and nodes
- Your apps in the same graph: move an app to another playback device, set its volume, see who records from a loopback

**Everyday control**
- Gain, 48V, INST, MON and mute for each input; output levels, jacks and +17 dBu
- Presets — optionally including Windows routing — and automatic presets that switch when an app starts
- Global hotkeys that work while a game has focus, a compact mini mode, a tray icon
- Keeps the device's own memory up to date, so it starts with your setup even without a PC

**Built to stay out of the way**
- Starts with Windows in the tray, uses almost no CPU when hidden
- Dark and light themes, English and Korean
- Updates itself from GitHub Releases in the background
- One-click problem report: logs bundled into a file you can paste straight into a chat

**Optional: extra Windows devices**
- More playback and recording devices on the driver's virtual channels, routed like any other node
  (needs a plugin file you provide — see below)

## Download

1. Get **`TPConsole-Setup-x.y.z.exe`** from the [latest release](https://github.com/gkfyddl2662/TPConsole/releases/latest).
2. Run it and click **Install**. Windows may show a SmartScreen notice for a new app: *More info → Run anyway*.
3. Open TPConsole from the Start menu.

**Requirements:** Windows 10 or 11 (x64) · TOPPING E2x2 OTG · TOPPING USB audio driver 5.74
(installed with TOPPING Professional Control Center). No .NET installation needed.

TPConsole runs as administrator because it changes Windows audio devices. Installed copies update
automatically; you can turn that off in *Settings → Updates*.

## Extra devices (virtual routing)

Extra playback and recording devices use Thesycon's DSP mixer plugin, `tusbaudiodsp_mixer.sys`.
It belongs to another company, so it is **not included**. If you have a copy that matches your driver
version (5.74), choose it once in *Settings → Virtual routing → Choose file*, then *Install*.

## What's new

See [Releases](https://github.com/gkfyddl2662/TPConsole/releases) — every release lists its changes.

## Something wrong?

*Settings → Advanced → Diagnostics → Copy log file* puts versions and recent logs into one file on the
clipboard. Paste it (Ctrl+V) into an issue, Discord or an email. Your Windows user folder is hidden.

## Build from source

Requires the .NET 10 SDK and Node.js 22.

```powershell
powershell -ExecutionPolicy Bypass -File tools\package.ps1
```

The installer lands in `dist\`. Every push to `main` that changes code is built and published as a
release by [GitHub Actions](.github/workflows/release.yml).

## Disclaimer

TPConsole is an independent project. It is not affiliated with, endorsed by or supported by TOPPING.
"TOPPING" and "E2x2 OTG" are trademarks of their owner. Firmware updates are possible but best done with
the official Control Center. Use at your own risk.
