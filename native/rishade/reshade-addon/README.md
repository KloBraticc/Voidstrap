# RiShade ReShade Add-on

ReShade 6.8 add-on (`rishade_reshade_addon.dll`) bridging ReShade with Voidstrap's compositor. Based on Unishade's add-on architecture by OMouta.

## How it works

- Binds captured game frames as ReShade's `COLOR` input.
- Renders active shader passes directly into the compositor stage render target.
- Draws the ReShade overlay menu into the swapchain backbuffer on presentation.
- Syncs overlay visibility and preset changes with Voidstrap.

## Credits

- [Unishade](https://github.com/OMouta/Unishade) by OMouta
