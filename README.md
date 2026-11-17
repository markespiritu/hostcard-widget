# IP Widget

A small desktop widget for Windows 11 that shows which machine you're on:
**hostname, IP address, MAC address, type (LAN/WLAN) and connection speed** for one network adapter.
Handy when you're hopping between several PCs over Moonlight/RDP and can't remember which is which.

## Use it

1. Copy `IpWidget.exe` anywhere on the machine (it's a single portable file, no install, no .NET needed).
2. Run it. The widget appears at the top-right of the main screen, sitting on the desktop underneath your windows.

| Action | How |
| --- | --- |
| Move it | Hold **Ctrl** and drag it. The border turns blue while Ctrl is held. Position is remembered. |
| Resize it | Hold **Ctrl** and scroll the mouse wheel over it, or hold **Ctrl** and drag its bottom-right corner. Size is remembered; menu → **Reset size** goes back to 100%. |
| Options menu | Hold **Ctrl** and right-click it. |
| Pick the adapter | Menu → **Adapter**. *Automatic* follows whichever adapter Windows uses for internet traffic. |
| Show it above windows | Menu → **Keep on top of other windows** |
| Start with Windows | Menu → **Start with Windows** (adds a shortcut to your Startup folder) |
| Lost it off-screen | Menu → **Reset position** (it also resets itself if its saved spot is no longer on any screen) |
| Quit | Menu → **Exit** |

Without Ctrl held, the widget ignores the mouse completely, so clicks go through to the desktop or window beneath it.

The info refreshes as soon as Windows reports a network change, and every 15 seconds anyway (Wi-Fi link speed moves around).

### Two adapters at once

Run the exe a second time. Each copy takes the next free **slot** (1, 2, 3 …) and keeps its own adapter, position
and on-top setting, so you can have one widget for Ethernet and one for Wi-Fi.
You can also choose the slot yourself: `IpWidget.exe --slot 2`. The "Start with Windows" shortcut for each slot
launches that slot, so both come back after a reboot.

Settings live in `%APPDATA%\IpWidget\slot1.json`, `slot2.json`, …

## Build

Needs the .NET 8 SDK.

```powershell
.\build.ps1              # -> dist\IpWidget.exe (x64)
.\build.ps1 -Arch arm64  # -> dist\arm64\IpWidget.exe
```
