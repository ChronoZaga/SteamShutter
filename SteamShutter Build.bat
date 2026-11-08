@echo off
setlocal

rem Builds SteamShutter.exe (x64) from SteamShutter.cs and SteamShutter.ico in this
rem batch file's folder, using the C# compiler built into .NET Framework 4.8.
rem Creates SteamShutter.ini with the defaults first, unless one already exists.

pushd "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if not exist "%CSC%" (
    echo C# compiler not found. .NET Framework 4.8 x64 is required.
    popd
    pause
    exit /b 1
)

for %%F in (SteamShutter.cs SteamShutter.ico) do (
    if not exist "%%F" (
        echo Missing %%F
        popd
        pause
        exit /b 1
    )
)

if exist "SteamShutter.ini" (
    echo Keeping existing SteamShutter.ini
) else (
    > "SteamShutter.ini" (
        echo [SteamShutter]
        echo ; Exe to watch for disk writes. ".exe" is optional.
        echo ProcessName=steam
        echo ; Minutes of quiet before shutdown, 1 to 1440.
        echo TimerMinutes=5
        echo ; Download speed in MB/s that counts as busy.
        echo DownloadThresholdMBps=1
        echo ; Disk write speed in MB/s that counts as busy.
        echo DiskWriteThresholdMBps=1
    )
    if not exist "SteamShutter.ini" (
        echo Could not build SteamShutter.ini
        popd
        pause
        exit /b 1
    )
    echo Built SteamShutter.ini...
)

"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /warn:4 ^
 /out:SteamShutter.exe ^
 /win32icon:SteamShutter.ico ^
 /resource:SteamShutter.ico,SteamShutter.app.ico ^
 /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
 SteamShutter.cs

if errorlevel 1 (
    echo Build failed.
    popd
    pause
    exit /b 1
)

echo Built SteamShutter.exe...
popd
pause
exit /b 0
