# RiShade

Runs ReShade directly inside Voidstrap's compositor without injecting into Roblox. Based on [Unishade](https://github.com/OMouta/Unishade) by OMouta (huge credit to him for his original work in C++).

## How it works

Voidstrap captures the Roblox window with Windows Graphics Capture (WGC) and runs ReShade 6.8 effects directly on D3D11 swapchain frames in the compositor before Anti-Aliasing, motion blur, and Frame Gen. No hooks or DLL injection into Roblox.

On first enable, Voidstrap grabs ReShade 6.8 and official shader packs into `Extensions/RiShade`, keeping your existing presets and configs safe.

## Building

```sh
cargo build --release --manifest-path native/rishade/Cargo.toml
```

## Credits

- [Unishade](https://github.com/OMouta/Unishade) by OMouta
- [ReShade](https://reshade.me) by crosire
