using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Steamworks;

namespace ScreenshotWrapper
{
    static class Program
    {
        static IntPtr childHwnd;
        static Callback<ScreenshotRequested_t> screenshotReadyCallback;

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length == 0) return;

            if (!SteamAPI.Init())
            {
                MessageBox.Show("Steam API cannot be initialized, exiting.");
                return;
            }

            string exeName = args[0];
            string[] procArgs = args.Skip(1).ToArray();
            string procArgString = procArgs.Length == 0 ? string.Empty : string.Join(" ", procArgs);
            Process proc = Process.Start(exeName, procArgString);
            if (proc != null)
            {
                // Set up screenshot callback and hook
                screenshotReadyCallback = Callback<ScreenshotRequested_t>.Create(HandleScreenshotRequest);
                SteamScreenshots.HookScreenshots(true);

                // Set up global keyboard hook
                IntPtr hook = PlatformInvokeUSER32.SetWindowsHookEx(PlatformInvokeUSER32.HookType.WH_KEYBOARD_LL, KeyboardProc,
                    IntPtr.Zero, 0);
                if (hook == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    MessageBox.Show($"Failed to set hook, error {err}");
                }
                // Wait for window to be created before setting main window handle
                // You might need to change this if your game creates several windows
                // before settling.
                while (proc.MainWindowHandle == IntPtr.Zero && !proc.HasExited)
                {
                    System.Threading.Thread.Sleep(100);
                }
                childHwnd = proc.MainWindowHandle;
                // Run callbacks until child process exits
                while (!proc.HasExited)
                {
                    SteamAPI.RunCallbacks();
                    System.Threading.Thread.Sleep(16);
                }
                // Unhook before we leave
                if (hook != IntPtr.Zero) PlatformInvokeUSER32.UnhookWindowsHookEx(hook);
            }
            else
            {
                MessageBox.Show("Failed to start game process.");
            }

            SteamAPI.Shutdown();
        }

        static IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                // Since this is a global hook, we don't want to react to
                // keypress on every window, so make sure game window is foreground
                if (PlatformInvokeUSER32.GetForegroundWindow() == childHwnd)
                {
                    if ((int)wParam == PlatformInvokeUSER32.WM_KEYDOWN)
                    {
                        var kbdInfo = (PlatformInvokeUSER32.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(PlatformInvokeUSER32.KBDLLHOOKSTRUCT));
                        Keys key = (Keys)kbdInfo.vkCode;
                        if (key == Keys.F11)
                        {
                            // This does not seem to actually work when the overlay isn't loaded
                            //SteamScreenshots.TriggerScreenshot();
                            HandleScreenshotRequest(new ScreenshotRequested_t());
                        }
                    }
                }
            }
            return PlatformInvokeUSER32.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        static void HandleScreenshotRequest(ScreenshotRequested_t p)
        {
            if (childHwnd != IntPtr.Zero)
            {
                bool success = TakeScreenshot(childHwnd);
                if (!success)
                {
                    MessageBox.Show("Failed to take screenshot.");
                }
            }
        }

        static bool TakeScreenshot(IntPtr hwnd)
        {
            bool success = false;
            IntPtr hBmp = IntPtr.Zero;
            // Get DC of game window
            IntPtr hDC = PlatformInvokeUSER32.GetDC(hwnd);
            if (hDC != IntPtr.Zero)
            {
                // Make compatible DC that we can blit to
                IntPtr hMemDC = PlatformInvokeGDI32.CreateCompatibleDC(hDC);
                if (hMemDC != IntPtr.Zero)
                {
                    // Get size of what we're trying to take a screenshot of
                    if (PlatformInvokeUSER32.GetClientRect(hwnd, out PlatformInvokeUSER32.RECT rect))
                    {
                        int width = rect.right - rect.left;
                        int height = rect.bottom - rect.top;
                        // Create bitmap
                        hBmp = PlatformInvokeGDI32.CreateCompatibleBitmap(hDC, width, height);
                        if (hBmp != IntPtr.Zero)
                        {
                            // Blit the thing
                            IntPtr hOld = PlatformInvokeGDI32.SelectObject(hMemDC, hBmp);
                            PlatformInvokeGDI32.BitBlt(hMemDC, 0, 0, width, height, hDC, 0, 0, PlatformInvokeGDI32.SRCCOPY);
                            PlatformInvokeGDI32.SelectObject(hMemDC, hOld);
                            success = true;
                        }
                    }
                    PlatformInvokeGDI32.DeleteDC(hMemDC);
                }
                PlatformInvokeUSER32.ReleaseDC(hwnd, hDC);
            }

            if (success)
            {
                // Use .NET's graphics processing to extract our pixels
                using (Bitmap bmp = Image.FromHbitmap(hBmp))
                {
                    // Remember to clean up the native bitmap from earlier
                    PlatformInvokeGDI32.DeleteObject(hBmp);

                    // Lock the bitmap data and prepare a buffer
                    BitmapData bits = bmp.LockBits(new Rectangle(Point.Empty, bmp.Size), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                    byte[] buffer = new byte[bmp.Width * bmp.Height * 3];
                    int i = 0;

                    unsafe
                    {
                        byte* scanline = (byte*)bits.Scan0;
                        while (i < buffer.Length)
                        {
                            for (int j = 0; j < bits.Width; ++j)
                            {
                                // The buffer in .NET is BGR, so flip it to RGB when copying
                                buffer[i++] = *(scanline + 2);
                                buffer[i++] = *(scanline + 1);
                                buffer[i++] = *(scanline);
                                scanline += 3;
                            }
                            scanline += bits.Stride - bits.Width * 3;
                        }
                    }

                    // Send it off to Steam
                    ScreenshotHandle hScreenshot = SteamScreenshots.WriteScreenshot(buffer, (uint)buffer.Length, bmp.Width, bmp.Height);
                    success = hScreenshot != ScreenshotHandle.Invalid;
                }
            }

            return success;
        }
    }
}
