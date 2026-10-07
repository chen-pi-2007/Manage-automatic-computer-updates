# 启动程序、等待指定秒数、截下主窗口、结束程序。开发时核对界面用。
# 用 PrintWindow 只截程序自己的窗口：就算被别的窗口挡住，也不会截到别的内容。
param([string]$Exe, [string]$Out, [string]$Arguments = "", [int]$WaitSeconds = 5)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W {
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[W]::SetProcessDPIAware() | Out-Null
# 已经有一个在运行时，新启动的会把它调出来然后自己退出，截到的就不是这次的参数和设置
if (Get-Process UpdateHelper -ErrorAction SilentlyContinue) { "已有更新管理小助手在运行，请先退出它"; exit 1 }
$p = if ($Arguments) { Start-Process -FilePath $Exe -ArgumentList $Arguments -PassThru } else { Start-Process -FilePath $Exe -PassThru }
$h = [IntPtr]::Zero
for ($i = 0; $i -lt 60 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 250; $p.Refresh(); $h = $p.MainWindowHandle }
if ($h -eq [IntPtr]::Zero) { "没有找到窗口"; $p | Stop-Process -Force; exit 1 }
Start-Sleep -Seconds $WaitSeconds
$r = New-Object W+R
[W]::GetWindowRect($h, [ref]$r) | Out-Null
$bmp = New-Object System.Drawing.Bitmap ($r.Rt - $r.L), ($r.B - $r.T)
$g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
[W]::PrintWindow($h, $dc, 2) | Out-Null   # 2 = PW_RENDERFULLCONTENT，WPF/云母窗口也能截全
$g.ReleaseHdc($dc); $g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
"已截图 $($bmp.Width)x$($bmp.Height)"
$p | Stop-Process -Force
