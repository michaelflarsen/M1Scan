Start M1Scan, vent til UI er klar, og tag et screenshot så du kan se og beskrive appen.

## Trin 1 — Stop evt. kørende instans
```powershell
Stop-Process -Name "M1Scan" -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
```

## Trin 2 — Build (Debug)
```powershell
cd "c:\VS_Code_projekt\M1Scan"
dotnet build --configuration Debug
```
Hvis build fejler: stop og rapporter fejlen til brugeren. Fortsæt ikke.

## Trin 3 — Start M1Scan (elevated)
Appen kræver admin-rettigheder — start exe direkte med RunAs:
```powershell
Start-Process "c:\VS_Code_projekt\M1Scan\bin\Debug\net8.0-windows\M1Scan.exe" -Verb RunAs
```

## Trin 4 — Vent til UI er klar
```powershell
Start-Sleep -Seconds 8
```

## Trin 5 — Tag screenshot (kun M1Scan-vinduet)
```powershell
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32 {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
$proc = Get-Process -Name "M1Scan" | Select-Object -First 1
$r = New-Object Win32+RECT
[Win32]::GetWindowRect($proc.MainWindowHandle, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g   = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, [System.Drawing.Size]::new($w, $h))
$out = "c:\VS_Code_projekt\M1Scan\ScreenShot\live.png"
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output $out
```

## Trin 6 — Vis og beskriv screenshot
Brug Read-værktøjet til at åbne `c:\VS_Code_projekt\M1Scan\ScreenShot\live.png`.
Beskriv hvad du ser:
- Hvilken fane/sektion der er aktiv
- Om den forventede ændring er synlig og ser korrekt ud
- Eventuelle synlige fejl, layoutproblemer eller uventede elementer
