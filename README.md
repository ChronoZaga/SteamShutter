// ============================================================================
// Steam Shutter
// ============================================================================
//
// Turns your PC off after Steam finishes downloading.
//
// HOW TO USE IT
//   1. Start your download in Steam.
//   2. Open SteamShutter.exe and walk away.
//   3. Once Steam has been quiet for 5 minutes, your PC turns off.
//   To cancel, close the Steam Shutter window. Your PC won't go to sleep
//   while it's open.
//
// WARNING
//   Open programs are closed without asking. Save your work first!
//
// HOW THE TIMER WORKS
//   Every second, it checks whether Steam is busy: downloading, or saving
//   files. All downloads from any program count, not just Steam.
//   1. When things are quiet, the 5-minute countdown starts, even if Steam
//      isn't open.
//   2. Once started, it keeps counting down, even through short bursts.
//   3. If Steam is busy for 5 seconds in a row, the timer goes back to
//      5:00 and waits until things are quiet again.
//   4. At 0:00, the PC shuts down. If it's still on, it tries again every
//      2 minutes.
//
// SETTINGS (optional)
//   Open SteamShutter.ini in Notepad to change the wait time (TimerMinutes)
//   or the speed limits. Save it, then reopen Steam Shutter.
//
// BUILDING IT
//   Double-click "SteamShutter Build.bat". Needs Windows 10 or 11, 64-bit.
// ============================================================================
