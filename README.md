# Railroader Anti-Aliasing Options

A client-side DLL mod for [Railroader](https://store.steampowered.com/app/1683150/Railroader/)
0.6 (experimental branch) that adds the anti-aliasing the game doesn't expose, to tame
shimmering, crawling rails.

The game only offers MSAA (Off / 2x / 4x). MSAA smooths geometry edges, but distant rails are
thinner than a pixel and mostly shimmer from their thin shape and shiny tops. This mod switches
on Unity URP's built-in TAA, SMAA and FXAA on the game camera, lets you control MSAA, and adds
supersampling, all switchable live in game.

## Features

- **TAA** with four stability presets, from sharp to very stable
- **SMAA** (high quality) and **FXAA**
- **MSAA** 1x / 2x / 4x for the non-temporal modes
- **Supersampling** 1.0x to 2.0x (the game's own render scale stops at 1.0)
- On-screen readout when you change anything; every change is saved to `config.json`

## Hotkeys

| Key | Action |
|---|---|
| **F10** | AA mode: Off → FXAA → SMAA → TAA |
| **Shift+F10** | MSAA: 1x → 2x → 4x (ignored in TAA mode) |
| **F11** | TAA stability: sharp → balanced → stable → verystable |
| **Shift+F11** | Supersampling: 1.0x → 1.25x → 1.5x → 1.75x → 2.0x |

Only F10/F11 are used, because Railroader binds F1–F8 (F5–F8 are fly-camera presets).

## Install

1. Download `railroader-antialiasing-<version>.zip` from
   [Releases](https://github.com/londospark/railroader-antialiasing/releases).
2. Extract the `londospark-taa` folder into your Railroader `Mods` folder:
   - **Windows:** `Documents\Railroader\Mods\`
   - **Linux (Proton):** `<SteamLibrary>/steamapps/compatdata/1683150/pfx/drive_c/users/steamuser/Documents/Railroader/Mods/`
3. Start the game and turn on **Preferences → Features → Experimental → Allow Unsafe DLL Mods**.
4. Enable **Anti-Aliasing Options** in the main menu's **Mods** screen, then restart the game.
5. Load a save. A bar at the bottom of the screen shows the current settings for a few seconds.

The mod's log lines appear in `Player.log`
(`AppData\LocalLow\Giraffe Lab LLC\Railroader\Player.log`), tagged `ModPack: "londospark/taa"`.

## Which setting to use

- **TAA** smooths thin rails best, but its sub-pixel jitter can make them crawl. Step through
  **F11** until the rails settle. Steadier presets keep more history and jitter less, which
  gives a softer image and slight trails while the camera moves.
- **SMAA + MSAA 4x** gives a sharper image with no trails. It handles geometry edges well, but not
  sub-pixel rails.
- **Supersampling** costs a lot of GPU (1.5x ≈ 2.25x the pixels). Only **2.0x** gives a clean
  2x2 average with URP's downscale filter.

On an RTX 4070 Laptop GPU at 2560x1600, TAA at native resolution held 80 fps (capped) at about 82%
GPU load.

## config.json

Lives next to the DLL. Edit it with the game closed, or just use the hotkeys.

| Key | Values | Meaning |
|---|---|---|
| `mode` | `off`, `fxaa`, `smaa`, `taa` | AA method |
| `taaPreset` | `sharp`, `balanced`, `stable`, `verystable`, `custom` | TAA stability; `custom` uses the `taa*` values below |
| `taaQuality` | 0–4 | URP TAA quality (custom preset) |
| `taaBaseBlendFactor` | 0–1 | Lower keeps more history: smoother, more ghosting (custom) |
| `taaVarianceClampScale` | 0.6–1.2 | Higher rejects less history (custom) |
| `taaJitterScale` | 0–1 | Lower = less sub-pixel jitter (custom) |
| `taaSharpening` | 0–1 | Sharpening after TAA (custom) |
| `msaa` | 1, 2, 4 | MSAA for non-TAA modes |
| `renderScale` | 0.5–2.0 | Above 1 supersamples |
| `smaaQuality` | 0–2 | SMAA Low/Medium/High |
| `showOverlay` | true/false | Show the settings bar on change |
| `detachCameraStack` | true/false | Experimental: unstacks overlay cameras so TAA can run (Railroader 0.6 has none) |

## Limitations

- **No DLSS, FSR 2+ or XeSS.** The game doesn't ship the vendor upscaler libraries.
- **No STP.** Unity's STP upscaler isn't available either: the game build strips its shaders, and
  URP throws an error every frame, so the mod maps `stp` to `taa`.
- The mod overrides the in-game Anti-Aliasing setting while it's loaded. TAA always runs with
  MSAA off, because URP can't combine the two.
- Built against Railroader 0.6.2 (Unity 6.3, URP 17). Future game updates may break it.

## Building

Needs the .NET SDK (any version that can target `netstandard2.1`) and a Railroader install for
the reference assemblies:

```bash
cd src
dotnet build -c Release -o ../build -p:GameManaged="/path/to/Railroader/Railroader_Data/Managed"
```

Copy `build/RailroaderTaa.dll`, `mod/Info.json` and `mod/config.json` into
`Mods/londospark-taa/`.

## License

MIT. See [LICENSE](LICENSE). Not affiliated with Giraffe Lab LLC.
