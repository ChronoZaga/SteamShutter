# Steam Shutter

Turns your PC off after Steam finishes downloading.

## How to use it

1. Start your download in Steam.
2. Open `SteamShutter.exe` and walk away.
3. Once Steam has been quiet for 5 minutes, your PC turns off.

To cancel, close the Steam Shutter window. Your PC won't go to sleep while it's open.

> [!WARNING]
> Open programs are closed without asking. Save your work first!

## How the timer works

Every second, it checks whether Steam is busy: downloading, or saving files, at 1 MB/s or more. Downloads from any program count, not just Steam.

1. When things are quiet, the 5-minute countdown starts, even if Steam isn't open.
2. Once started, it keeps counting down, even through short bursts.
3. If Steam is busy for 5 seconds in a row, the timer goes back to 5:00 and waits until things are quiet again.
4. At 0:00, the PC shuts down. If it's still on, it tries again every 2 minutes.

## Settings (optional)

Open `SteamShutter.ini` in Notepad, change a value, save it, then reopen Steam Shutter.

| Setting | Default | What it does |
|---|---|---|
| `ProcessName` | `steam` | The program to watch |
| `TimerMinutes` | `5` | Minutes to wait, from 1 to 1440 |
| `DownloadThresholdMBps` | `1` | Download speed that counts as busy |
| `DiskWriteThresholdMBps` | `1` | File-saving speed that counts as busy |

## Building it

Double-click `SteamShutter Build.bat`. Needs Windows 10 or 11, 64-bit.
