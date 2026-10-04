# UxPlay window auto-resize helper
# Watches the UxPlay video window, detects letterbox (black bars) when the
# iPhone rotates, and reshapes the window to match the video aspect ratio.
param([switch]$Once, [switch]$Verbose, [switch]$DryRun)

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Collections.Generic;

public static class UxWin {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);

    public static IntPtr FindForPids(int[] pids) {
        IntPtr found = IntPtr.Zero; long best = 0;
        var set = new HashSet<uint>(); foreach (var p in pids) set.Add((uint)p);
        EnumWindows((h, l) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (set.Contains(pid) && IsWindowVisible(h)) {
                RECT r; GetClientRect(h, out r);
                long a = (long)(r.R - r.L) * (r.B - r.T);
                if (a > best) { best = a; found = h; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static bool Skip(IntPtr h) { return IsIconic(h) || IsZoomed(h); }

    // Returns [clientW, clientH, contentW, contentH] or null.
    public static int[] Measure(IntPtr h) {
        RECT c; GetClientRect(h, out c);
        int w = c.R, ht = c.B;
        if (w < 50 || ht < 50) return null;
        RECT wr; GetWindowRect(h, out wr);
        int ww = wr.R - wr.L, wh = wr.B - wr.T;
        using (var bmp = new Bitmap(ww, wh, PixelFormat.Format32bppArgb)) {
            using (var g = Graphics.FromImage(bmp)) {
                IntPtr hdc = g.GetHdc();
                PrintWindow(h, hdc, 3); // PW_CLIENTONLY | PW_RENDERFULLCONTENT
                g.ReleaseHdc(hdc);
            }
            var data = bmp.LockBits(new Rectangle(0, 0, ww, wh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int stride = data.Stride; byte[] px = new byte[stride * wh];
            Marshal.Copy(data.Scan0, px, 0, px.Length); bmp.UnlockBits(data);
            int cw = Math.Min(w, ww), ch = Math.Min(ht, wh);
            Func<int,int,bool> dark = (x, y) => {
                int i = y * stride + x * 4; return px[i] < 10 && px[i+1] < 10 && px[i+2] < 10;
            };
            Func<int,bool> rowDark = y => { for (int k = 1; k < 64; k++) if (!dark(cw * k / 64, y)) return false; return true; };
            Func<int,bool> colDark = x => { for (int k = 1; k < 64; k++) if (!dark(x, ch * k / 64)) return false; return true; };
            int top = 0; while (top < ch && rowDark(top)) top++;
            if (top >= ch) return null; // all black
            int bot = 0; while (bot < ch && rowDark(ch - 1 - bot)) bot++;
            int left = 0; while (left < cw && colDark(left)) left++;
            int right = 0; while (right < cw && colDark(cw - 1 - right)) right++;
            return new int[] { cw, ch, cw - left - right, ch - top - bot, top, bot, left, right };
        }
    }

    public static void Reshape(IntPtr h, int newCW, int newCH) {
        RECT wr, cr; GetWindowRect(h, out wr); GetClientRect(h, out cr);
        int fx = (wr.R - wr.L) - cr.R, fy = (wr.B - wr.T) - cr.B;
        MONITORINFO mi = new MONITORINFO(); mi.cbSize = Marshal.SizeOf(mi);
        GetMonitorInfo(MonitorFromWindow(h, 2), ref mi);
        int maxW = mi.rcWork.R - mi.rcWork.L - fx, maxH = mi.rcWork.B - mi.rcWork.T - fy;
        double s = Math.Min(1.0, Math.Min((double)maxW / newCW, (double)maxH / newCH));
        newCW = (int)(newCW * s); newCH = (int)(newCH * s);
        int W = newCW + fx, H = newCH + fy;
        int cx = (wr.L + wr.R) / 2, cy = (wr.T + wr.B) / 2;
        int x = Math.Max(mi.rcWork.L, Math.Min(cx - W / 2, mi.rcWork.R - W));
        int y = Math.Max(mi.rcWork.T, Math.Min(cy - H / 2, mi.rcWork.B - H));
        SetWindowPos(h, IntPtr.Zero, x, y, W, H, 0x0004 | 0x0010); // NOZORDER | NOACTIVATE
    }
}
"@

function Get-UxPids { @(Get-Process uxplay -ErrorAction SilentlyContinue | ForEach-Object Id) }

$lastAspect = 0; $seen = $false
while ($true) {
    $pids = Get-UxPids
    if ($pids.Count -eq 0) { if ($Once -or $seen) { break }; Start-Sleep -Seconds 2; continue }
    $seen = $true
    $h = [UxWin]::FindForPids([int[]]$pids)
    if ($h -ne [IntPtr]::Zero -and -not [UxWin]::Skip($h)) {
        $m = [UxWin]::Measure($h)
        if ($m) {
            $cw, $ch, $vw, $vh = $m[0..3]
            if ($Verbose) { "client ${cw}x${ch} content ${vw}x${vh} bars t$($m[4]) b$($m[5]) l$($m[6]) r$($m[7])" }
            # Only act on clear, symmetric letterboxing (bars on one axis only)
            $barsV = ($m[4] + $m[5]) -gt $ch * 0.08 -and [math]::Abs($m[4] - $m[5]) -le [math]::Max(4, $ch * 0.02) -and ($m[6] + $m[7]) -le 4
            $barsH = ($m[6] + $m[7]) -gt $cw * 0.08 -and [math]::Abs($m[6] - $m[7]) -le [math]::Max(4, $cw * 0.02) -and ($m[4] + $m[5]) -le 4
            if (($barsV -or $barsH) -and $vw -gt 20 -and $vh -gt 20) {
                $aspect = $vw / $vh
                # Keep the window's long side, shape it to the video
                $long = [math]::Max($cw, $ch)
                if ($aspect -ge 1) { $nw = $long; $nh = [int]($long / $aspect) } else { $nh = $long; $nw = [int]($long * $aspect) }
                if ($Verbose) { "-> reshape to ${nw}x${nh} (aspect $([math]::Round($aspect,3)))" }
                if (-not $DryRun) { [UxWin]::Reshape($h, $nw, $nh) }
                $lastAspect = $aspect
            }
        }
    }
    if ($Once) { break }
    Start-Sleep -Milliseconds 700
}


