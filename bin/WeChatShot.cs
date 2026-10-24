
// WeChatShot - a system-wide screenshot tool with a WeChat-style full-screen overlay.
// Compiled with the in-box .NET Framework compiler (C# 5 only: no interpolation, no ?.).
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace WeChatShot
{
    internal static class NativeApi
    {
        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(IntPtr process, uint flags, System.Text.StringBuilder name, ref uint size);

        public const int SRCCOPY = 0x00CC0020;
        public const int SW_HIDE = 0;
        public const int SW_SHOW = 5;
        public const int SW_MINIMIZE = 6;
        // Low-level keyboard hook: Shift must work even when this overlay does not own the keyboard.
        internal const int WH_KEYBOARD_LL = 13;
        internal const int WM_KEYDOWN_HOOK = 0x0100;
        internal const int WM_SYSKEYDOWN_HOOK = 0x0104;
        internal const int VK_SHIFT_HOOK = 0x10;
        internal const int VK_LSHIFT_HOOK = 0xA0;
        internal const int VK_RSHIFT_HOOK = 0xA1;
        internal delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        internal static extern IntPtr GetModuleHandle(string lpModuleName);
        public const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height, IntPtr hdcSrc, int xSrc, int ySrc, int rop);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public const int WM_SYSCOMMAND = 0x0112;
        public const int SC_MINIMIZE = 0xF020;
        public const int SC_RESTORE = 0xF120;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        /// <summary>
        /// Top-level windows in Z order (topmost first). Needed instead of WindowFromPoint:
        /// the capture overlay itself covers the whole screen and is topmost, so a hit test
        /// always returns the overlay �?the window underneath is only reachable by walking the
        /// Z order and skipping our own process.
        /// </summary>
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        /// <summary>VK_MENU (Alt) �?read at capture time, so the browser never has to detect it.</summary>
        public const int VK_MENU = 0x12;
        /// <summary>VK_SHIFT �?fallback modifier when Alt is swallowed by the browser menu.</summary>
        public const int VK_SHIFT = 0x10;

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out RECT rect, int size);

        /// <summary>GA_ROOT: the top-level window owning a child hit by WindowFromPoint.</summary>
        public const uint GA_ROOT = 2;
        /// <summary>DWMWA_EXTENDED_FRAME_BOUNDS: the visible frame, without the invisible resize border.</summary>
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        /// <summary>DWMWA_CLOAKED: non-zero for windows hidden by DWM (virtual desktops).</summary>
        public const int DWMWA_CLOAKED = 14;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("dwmapi.dll")]
        public static extern int DwmFlush();

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }

    internal static class ScreenGrab
    {
        public static Rectangle VirtualBounds()
        {
            return SystemInformation.VirtualScreen;
        }

        public static Bitmap Capture(Rectangle bounds)
        {
            if (bounds.Width < 1 || bounds.Height < 1)
            {
                throw new InvalidOperationException("virtual screen bounds are empty: " + bounds.ToString());
            }
            Bitmap bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                IntPtr dest = g.GetHdc();
                IntPtr src = NativeApi.GetDC(IntPtr.Zero);
                try
                {
                    bool ok = NativeApi.BitBlt(dest, 0, 0, bounds.Width, bounds.Height, src, bounds.X, bounds.Y, NativeApi.SRCCOPY);
                    if (!ok)
                    {
                        throw new InvalidOperationException("BitBlt failed, win32 error " + Marshal.GetLastWin32Error().ToString());
                    }
                }
                finally
                {
                    NativeApi.ReleaseDC(IntPtr.Zero, src);
                    g.ReleaseHdc(dest);
                }
            }
            return bmp;
        }
    }

    /// <summary>
    /// The "hide current window while capturing" step, the same idea as WeChat's
    /// 閵嗗本鍩呴崶鐐闂呮劘妫岃ぐ鎾冲缁愭褰涢妴宄磂tting: whatever window is in front is hidden, the desktop
    /// behind it is grabbed, and the window comes back when the overlay closes.
    /// </summary>
    internal static class Desktop
    {
        public static IntPtr HideForeground(out string note)
        {
            note = "";
            IntPtr foreground = NativeApi.GetForegroundWindow();
            IntPtr hideable = Hideable(foreground);
            if (hideable == IntPtr.Zero)
            {
                note = "nothing to hide (foreground " + Describe(foreground) + ")";
                return IntPtr.Zero;
            }
            NativeApi.ShowWindow(hideable, NativeApi.SW_HIDE);
            NativeApi.DwmFlush();
            Thread.Sleep(170);
            note = "hid " + Describe(hideable);
            return hideable;
        }

        public static void Restore(IntPtr hidden, IntPtr focusBack, out string note)
        {
            note = "";
            if (hidden == IntPtr.Zero) return;
            NativeApi.ShowWindow(hidden, NativeApi.SW_SHOW);
            if (NativeApi.IsIconic(hidden)) NativeApi.ShowWindow(hidden, NativeApi.SW_RESTORE);
            NativeApi.DwmFlush();
            note = "restored " + Describe(hidden) + " visible=" + (NativeApi.IsWindowVisible(hidden) ? "yes" : "NO");
            if (focusBack != IntPtr.Zero) NativeApi.SetForegroundWindow(focusBack);
        }

        private static IntPtr Hideable(IntPtr candidate)
        {
            if (candidate == IntPtr.Zero) return IntPtr.Zero;
            if (!NativeApi.IsWindowVisible(candidate)) return IntPtr.Zero;
            if (NativeApi.IsIconic(candidate)) return IntPtr.Zero;
            IntPtr shell = NativeApi.GetShellWindow();
            if (shell != IntPtr.Zero && candidate == shell) return IntPtr.Zero;
            string cls = ClassOf(candidate);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
            {
                return IntPtr.Zero;
            }
            NativeApi.RECT rect;
            if (!NativeApi.GetWindowRect(candidate, out rect)) return IntPtr.Zero;
            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;
            // Popups, menus, tooltips and our own 1x1 message window are not worth hiding.
            if (w < 200 || h < 200) return IntPtr.Zero;
            return candidate;
        }

        /// <summary>Exposed so the overlay can offer a keep/hide toggle only when it would work.</summary>
        internal static bool IsHideable(IntPtr hwnd)
        {
            return Hideable(hwnd) != IntPtr.Zero;
        }

        public static string ClassOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
            NativeApi.GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string Describe(IntPtr hwnd)
        {
            return "0x" + hwnd.ToInt64().ToString("X") + "[" + ClassOf(hwnd) + "]";
        }
    }

    internal sealed class ShotForm : Form
    {
        private Bitmap _shot;
        private bool _dragging;
        /// <summary>Right button went down on this overlay; cancel when it comes back up.</summary>
        private bool _rightDown;
        /// <summary>Where the size readout was last painted, so its old box can be repainted too.</summary>
        private Rectangle _sizeLabelRect;
        private Point _start;
        private Rectangle _selection;
        private bool _hasSelection;
        /// <summary>Hit area of the painted tick button; empty when there is no selection.</summary>
        private Rectangle _okRect;
        private bool _okHover;
        private readonly Font _hintFont;
        private readonly Font _sizeFont;
        private readonly float _scale;
        private bool _hidForeground;
        private readonly System.Windows.Forms.Timer _hoverTimer;
        private Rectangle _hover;
        private bool _hoverActive;
        private Rectangle _pressedHover;
        private Point _pressPoint;

        /// <summary>Dirty strip of the current WM_PAINT; painters skip work outside it.</summary>
        private Rectangle _paintClip;


        private readonly Button _toggleButton;
        private readonly IntPtr _targetWindow;
        /// <summary>Which frozen variant is displayed; the live window is never left hidden.</summary>
        private bool _windowHidden;
        /// <summary>Process whose top-level windows this overlay hides (the Harness window).</summary>
        public static int HidePid;



        /// <summary>Image path of the Harness executable, supplied by the host (--hide-exe).</summary>
        public static string HideExe;

        private static readonly System.Collections.Generic.Dictionary<uint, string> ImagePathCache =
            new System.Collections.Generic.Dictionary<uint, string>();
        private IntPtr[] _hostWindows = new IntPtr[0];
        private static IntPtr _shiftHook = IntPtr.Zero;
        /// <summary>Rooted on purpose: the GC moving this delegate mid-hook would crash the process.</summary>
        private static NativeApi.LowLevelKeyboardProc _shiftHookProc;
        private static ShotForm _shiftHookOwner;
        private static int _hookDebugCount;
        private bool _hostHidden;
        private bool _toggling;
        /// <summary>Whether the Harness window is currently part of the picture (Shift shown it).</summary>
        internal static bool HostPickable;
        private static IntPtr _lastHoverHwnd;
        private Bitmap _shotVisible;
        private Bitmap _shotHidden;
        /// <summary>Auto-repeat guard: one Shift press must toggle exactly once, never flicker.</summary>
        private int _lastToggleTicks;

        public Bitmap Result;

        public ShotForm(Bitmap shot, Rectangle bounds, bool hidForeground, IntPtr targetWindow)
        {
            _shot = shot;
            if (hidForeground) _shotHidden = shot; else _shotVisible = shot;
            _hidForeground = hidForeground;
            _targetWindow = targetWindow;
            _windowHidden = hidForeground;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            TopMost = true;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            KeyPreview = true;
            // Invisible until both frozen frames exist, so neither can contain this chrome.
            Opacity = 0;
            BackColor = Color.Black;
            Cursor = Cursors.Cross;
            Text = "WeChatShot";
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;

            _scale = 1f;
            try
            {
                if (DeviceDpi > 0) _scale = DeviceDpi / 96f;
            }
            catch (Exception)
            {
                _scale = 1f;
            }
            if (_scale < 0.75f) _scale = 0.75f;
            if (_scale > 4f) _scale = 4f;

            _hintFont = new Font("Microsoft YaHei UI", 9.5f * _scale, FontStyle.Regular, GraphicsUnit.Point);
            _sizeFont = new Font("Consolas", 10f * _scale, FontStyle.Bold, GraphicsUnit.Point);

            // WeChat-style window detection: while nothing is being dragged, follow the window
            // under the cursor so a single click can capture it whole.
            _hover = Rectangle.Empty;
            _hoverActive = true;
            _pressedHover = Rectangle.Empty;
            _hoverTimer = new System.Windows.Forms.Timer();
            // 25 ms: the highlight should track the pointer closely. Painting is cheap now (one
            // blit plus a small region), so this costs little and feels much snappier (was 45 ms).
            _hoverTimer.Interval = 25;
            _hoverTimer.Tick += delegate { PollHover(); };

            // The keep/hide choice lives here, on screen, instead of in a modifier gesture:
            // a keyboard modifier is swallowed by the browser long before it reaches us.
            _toggleButton = MakeButton("", false);
            _toggleButton.Click += delegate { ToggleMode(); };
            Controls.Add(_toggleButton);
            UpdateToggleButton();

        }

        /// <summary>
        /// Whether the overlay may toggle the target window. Deliberately loose: a window we
        /// hid ourselves is not "visible" any more, and requiring visibility here made the
        /// show-it-again step impossible.
        /// </summary>
        private bool TargetIsHideable()
        {
            if (_targetWindow == IntPtr.Zero) return false;
            if (!NativeApi.IsWindow(_targetWindow)) return false;
            uint ownerPid;
            NativeApi.GetWindowThreadProcessId(_targetWindow, out ownerPid);
            if (ownerPid == OwnProcessId) return false;
            // Skip the Harness window while it is hidden (clicking it would mean "capture my own
            // frozen picture"). Once Shift brings it back it is an ordinary window and may be
            // captured like any other.
            if (!HostPickable && IsHostWindow(ownerPid)) return false;
            return true;
        }

        private void UpdateToggleButton()
        {
            if (_toggleButton == null) return;
            bool hostMode = HidePid > 0 && _hostWindows.Length > 0;
            if (!hostMode && !TargetIsHideable())
            {
                _toggleButton.Visible = false;
                return;
            }
            bool hiddenNow = hostMode ? _hostHidden : _windowHidden;
            _toggleButton.Text = hiddenNow ? "\u663e\u793a\u7a97\u53e3 (Shift)" : "\u9690\u85cf\u7a97\u53e3 (Shift)";
            Size toggleText = TextRenderer.MeasureText(_toggleButton.Text, _toggleButton.Font);
            int w = toggleText.Width + (int)Math.Round(26 * _scale);
            int h = (int)Math.Round(30 * _scale);
            _toggleButton.Size = new Size(w, h);
            _toggleButton.Location = new Point(Math.Max(8, ClientSize.Width - w - (int)Math.Round(18 * _scale)), (int)Math.Round(18 * _scale));
            _toggleButton.Visible = true;
            _toggleButton.BringToFront();
        }

        /// <summary>
        /// Re-takes the frozen screenshot with the front window shown or hidden, the way WeChat's
        /// 閵嗗本鍩呴崶鐐闂呮劘妫岃ぐ鎾冲缁愭褰涢妴?works. The overlay hides itself for the grab so it cannot end up
        /// in its own picture, then comes back with the fresh bitmap.
        /// </summary>
        /// <summary>
        /// Switch between "with the current window" and "without it". The first switch captures
        /// the missing variant once (the live window is hidden for ~200ms and immediately put
        /// back shown and focused); every later switch is an in-memory bitmap swap, so the page
        /// never sees another visibility change and the overlay stays instant.
        /// </summary>
        private void ToggleTargetWindow()
        {
            if (!TargetIsHideable())
            {
                ShotApp.Log("toggle: target 0x" + _targetWindow.ToInt64().ToString("X") + " is not usable");
                return;
            }
            bool wantHidden = !_windowHidden;
            Bitmap next = EnsureVariant(wantHidden);
            if (next == null)
            {
                ShotApp.Log("toggle: capture of variant hidden=" + (wantHidden ? "1" : "0") + " failed");
                return;
            }
            _shot = next;
            _windowHidden = wantHidden;
            _hidForeground = wantHidden;
            _hasSelection = false;
            _selection = Rectangle.Empty;
            // The hidden variant no longer corresponds to what is on screen, so the stale
            // window highlight must go and automatic window detection must stay off until the
            // user switches back to the matching variant.
            _hover = Rectangle.Empty;
            _hoverActive = true;
            UpdateToggleButton();
            ShotApp.Log("toggle: showing hidden=" + (wantHidden ? "1" : "0")
                + " cachedVisible=" + ((_shotVisible != null) ? "1" : "0")
                + " cachedHidden=" + ((_shotHidden != null) ? "1" : "0"));
            Invalidate();
        }

        /// <summary>The requested frozen variant, captured on first use and cached afterwards.</summary>
        private Bitmap EnsureVariant(bool hidden)
        {
            if (hidden)
            {
                if (_shotHidden != null) return _shotHidden;
                _shotHidden = CaptureWindowVariant(true);
                return _shotHidden;
            }
            if (_shotVisible != null) return _shotVisible;
            _shotVisible = CaptureWindowVariant(false);
            return _shotVisible;
        }

        /// <summary>
        /// Grab the desktop with the target window hidden or shown. The overlay hides itself for
        /// the grab (it must not appear in its own picture) and the live window is ALWAYS put back
        /// shown and focused before returning.
        /// </summary>
        private Bitmap CaptureWindowVariant(bool hideWindow)
        {
            if (_hoverTimer != null) _hoverTimer.Stop();
            Bitmap fresh = null;
            try
            {
                Hide();
                Application.DoEvents();
                NativeApi.ShowWindow(_targetWindow, hideWindow ? NativeApi.SW_HIDE : NativeApi.SW_SHOW);
                NativeApi.DwmFlush();
                Thread.Sleep(200);
                fresh = ScreenGrab.Capture(Bounds);
            }
            catch (Exception ex)
            {
                ShotApp.Log("variant capture failed: " + ex.Message);
                fresh = null;
            }
            finally
            {
                NativeApi.ShowWindow(_targetWindow, NativeApi.SW_SHOW);
                if (NativeApi.IsIconic(_targetWindow)) NativeApi.ShowWindow(_targetWindow, NativeApi.SW_RESTORE);
                NativeApi.DwmFlush();
                NativeApi.SetForegroundWindow(_targetWindow);
                Show();
                BringToFront();
                Activate();
                if (_hoverTimer != null) _hoverTimer.Start();
            }
            return fresh;
        }

        /// <summary>
        /// The one and only action button: a round tick. Deliberately nothing else �?cancelling is
        /// the right mouse button (and Esc), so a second button would only add chrome.
        /// </summary>
        private Button MakeCheckButton()
        {
            int d = (int)Math.Round(44 * _scale);
            Button b = new Button();
            b.Text = string.Empty;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.TabStop = false;
            b.Cursor = Cursors.Hand;
            b.Visible = false;
            b.AutoSize = false;
            b.Size = new Size(d, d);
            b.BackColor = Color.FromArgb(255, 51, 200, 180);
            using (GraphicsPath round = new GraphicsPath())
            {
                round.AddEllipse(0, 0, d, d);
                b.Region = new Region(round);
            }
            b.Paint += delegate(object sender, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float w = b.Width;
                float h = b.Height;
                using (Pen tick = new Pen(Color.FromArgb(255, 10, 20, 22), Math.Max(2f, 3.2f * _scale)))
                {
                    tick.StartCap = LineCap.Round;
                    tick.EndCap = LineCap.Round;
                    tick.LineJoin = LineJoin.Round;
                    g.DrawLines(tick, new PointF[]
                    {
                        new PointF(w * 0.30f, h * 0.52f),
                        new PointF(w * 0.44f, h * 0.66f),
                        new PointF(w * 0.71f, h * 0.36f),
                    });
                }
            };
            b.MouseEnter += delegate { b.BackColor = Color.FromArgb(255, 105, 218, 202); };
            b.MouseLeave += delegate { b.BackColor = Color.FromArgb(255, 51, 200, 180); };
            return b;
        }

        private Button MakeButton(string text, bool primary)
        {
            Button b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.TabStop = false;
            b.Visible = false;
            b.AutoSize = false;
            int h = (int)Math.Round(30 * _scale);
            int w = (int)Math.Round(88 * _scale);
            b.Size = new Size(w, h);
            b.Font = new Font("Microsoft YaHei UI", 9f * _scale, FontStyle.Regular, GraphicsUnit.Point);
            b.BackColor = primary ? Color.FromArgb(255, 51, 200, 180) : Color.FromArgb(48, 52, 60);
            b.ForeColor = primary ? Color.FromArgb(255, 10, 20, 22) : Color.FromArgb(255, 231, 243, 246);
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = primary ? Color.FromArgb(255, 51, 200, 180) : Color.FromArgb(41, 130, 230, 215);
            return b;
        }


        /// <summary>Observe Shift system-wide; the overlay may not hold the keyboard.</summary>
        private void InstallShiftHook()
        {
            if (_shiftHook != IntPtr.Zero) return;
            try
            {
                _shiftHookProc = HookCallback;
                _shiftHookOwner = this;
                _shiftHook = NativeApi.SetWindowsHookEx(NativeApi.WH_KEYBOARD_LL, _shiftHookProc, NativeApi.GetModuleHandle(null), 0);
                ShotApp.Log("shift hook: " + (_shiftHook != IntPtr.Zero ? "installed" : "FAILED"));
            }
            catch (Exception ex) { ShotApp.Log("shift hook: error " + ex.Message); }
        }

        private void RemoveShiftHook()
        {
            if (_shiftHook == IntPtr.Zero) return;
            try { NativeApi.UnhookWindowsHookEx(_shiftHook); }
            catch (Exception) { }
            _shiftHook = IntPtr.Zero;
            _shiftHookOwner = null;
        }

        /// <summary>Runs on the hook thread; hand the toggle back to the UI thread.</summary>
        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && _shiftHookOwner != null)
                {
                    int message = wParam.ToInt32();
                    if (message == NativeApi.WM_KEYDOWN_HOOK || message == NativeApi.WM_SYSKEYDOWN_HOOK)
                    {
                        int vk = Marshal.ReadInt32(lParam);
                        if (_hookDebugCount < 3)
                        {
                            _hookDebugCount++;
                            ShotApp.Log("hook: saw key vk=0x" + vk.ToString("X2"));
                        }
                        if (vk == NativeApi.VK_SHIFT_HOOK || vk == NativeApi.VK_LSHIFT_HOOK || vk == NativeApi.VK_RSHIFT_HOOK)
                        {
                            ShotForm owner = _shiftHookOwner;
                            if (owner != null && !owner.IsDisposed)
                            {
                                owner.BeginInvoke((MethodInvoker)delegate { owner.ShiftFromHook(); });
                            }
                        }
                    }
                }
            }
            catch (Exception) { }
            return NativeApi.CallNextHookEx(_shiftHook, nCode, wParam, lParam);
        }

        /// <summary>Shift from the hook: same debounced toggle as the in-overlay key path.</summary>
        private void ShiftFromHook()
        {
            ShotApp.Log("shift: hook -> toggling");
            ToggleMode();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Full screen on purpose. A 2 px inset keeps Wallpaper Engine from pausing its live
            // wallpaper, but it also leaves a live strip along two edges and clips the overlay:
            // the flash is the lesser evil, so the overlay covers the whole virtual screen.
            Bounds = ScreenGrab.VirtualBounds();
            _hoverTimer.Start();
            PrepareHostVariants();
            // Build the paint caches (scaled veil bitmap + GDI mirrors) before the overlay is
            // interactive: doing it lazily in the first OnPaint showed up as a 40-70 ms hitch on
            // the very first frame of the first drag.
            EnsureVeilCache();
            EnsureShotDdb();
            InstallShiftHook();
            ShotApp.Log("open: shown after " + ((int)(DateTime.Now - Program.Started).TotalMilliseconds) + " ms");
            Opacity = 1;
            ForceForeground();
        }

        /// <summary>
        /// Take the foreground so Enter, Esc and right-click reach this overlay. Windows may
        /// refuse the first request while the previous owner is still foreground, so retry once.
        /// </summary>
        private void ForceForeground()
        {
            try
            {
                Activate();
                BringToFront();
                Focus();
                NativeApi.SetForegroundWindow(Handle);
                System.Windows.Forms.Timer retry = new System.Windows.Forms.Timer();
                retry.Interval = 90;
                retry.Tick += delegate
                {
                    retry.Stop();
                    retry.Dispose();
                    try { Activate(); Focus(); NativeApi.SetForegroundWindow(Handle); }
                    catch (Exception) { }
                };
                retry.Start();
            }
            catch (Exception) { }
        }

        /// <summary>Hide every visible top-level window of the Harness process.</summary>

        /// <summary>
        /// Minimize the Harness window once, capture the clean frame, and keep the pre-minimize
        /// frame as the other variant. Runs while this overlay is still fully transparent.
        /// </summary>
        /// <summary>
        /// PIDs treated as "the Harness itself": the pid the host passed plus every ancestor of
        /// this process. The walking is needed because the desktop app puts a wrapper process
        /// (--expose-internals) between the window owner and the node runtime, so process.ppid
        /// points at the wrapper and a single-pid match finds no window at all (2026-10-19).
        /// </summary>
        /// <summary>
        /// Minimize a foreign window by asking its owner to do it. `ShowWindow(SW_MINIMIZE)` is
        /// unreliable here: the first ShowWindow call of a process is overridden by STARTUPINFO's
        /// wShowWindow, and Electron windows ignore a foreign minimize anyway (both observed
        /// 2026-10-19: state stayed VIS/vis while the same call from PowerShell minimized it).
        /// </summary>
        internal static void MinimizeWindow(IntPtr hwnd)
        {
            NativeApi.PostMessage(hwnd, NativeApi.WM_SYSCOMMAND, new IntPtr(NativeApi.SC_MINIMIZE), IntPtr.Zero);
        }

        /// <summary>Bring a minimized window back, also by asking its owner.</summary>
        internal static void RestoreWindow(IntPtr hwnd)
        {
            NativeApi.ShowWindow(hwnd, NativeApi.SW_RESTORE);
            if (NativeApi.IsIconic(hwnd))
            {
                NativeApi.PostMessage(hwnd, NativeApi.WM_SYSCOMMAND, new IntPtr(NativeApi.SC_RESTORE), IntPtr.Zero);
            }
            if (!NativeApi.IsWindowVisible(hwnd)) NativeApi.ShowWindow(hwnd, NativeApi.SW_SHOW);
        }

        /// <summary>Image path of a process, cached; null when it cannot be read.</summary>
        internal static string ImagePathOf(uint pid)
        {
            string cached;
            if (ImagePathCache.TryGetValue(pid, out cached)) return cached;
            string result = null;
            IntPtr handle = NativeApi.OpenProcess(NativeApi.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle != IntPtr.Zero)
            {
                try
                {
                    System.Text.StringBuilder sb = new System.Text.StringBuilder(1024);
                    uint size = (uint)sb.Capacity;
                    if (NativeApi.QueryFullProcessImageName(handle, 0, sb, ref size)) result = sb.ToString(0, (int)size);
                }
                finally { NativeApi.CloseHandle(handle); }
            }
            ImagePathCache[pid] = result;
            return result;
        }

        /// <summary>
        /// Whether a window belongs to the Harness itself. Matched by pid first (the value the host
        /// passes) and then by executable image path, because the desktop app inserts a wrapper
        /// process between the window owner and the node runtime: process.ppid therefore does not
        /// name the window owner and a pid-only match minimized nothing (observed 2026-10-19).
        /// </summary>
        internal static bool IsHostWindow(uint pid)
        {
            if (HidePid > 0 && pid == (uint)HidePid) return true;
            if (string.IsNullOrEmpty(HideExe)) return false;
            string path = ImagePathOf(pid);
            return !string.IsNullOrEmpty(path) && string.Equals(path, HideExe, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Set when the overlay refuses to capture because the Harness window stayed visible.</summary>
        internal bool HostHideFailed { get { return _hostHideReason != null; } }
        internal string HostHideReason { get { return _hostHideReason; } }
        private string _hostHideReason;

        /// <summary>
        /// Whether a freshly captured frame differs from the reference inside the area the Harness
        /// window used to cover �?i.e. whether the window is really gone from the picture.
        /// Samples a grid rather than every pixel: this runs while the user waits for the overlay.
        /// </summary>

        /// <summary>
        /// Abort this capture: remember why, put the window back, and close so the caller can report
        /// it. Capturing anyway would hand the user a screenshot of their own chat window with no
        /// indication that anything went wrong.
        /// </summary>
        private void FailHostHide(string reason)
        {
            _hostHideReason = reason;
            ShotApp.Log("autohide: refusing to capture �?" + reason);
            try { ShowHostWindows(); } catch (Exception) { }
            BeginInvoke((MethodInvoker)delegate { Close(); });
        }

        /// <summary>
        /// Whether a freshly captured frame differs from the reference inside the area the Harness
        /// window used to cover �?i.e. whether the window is really gone from the picture.
        /// Samples a grid rather than every pixel: this runs while the user waits for the overlay.
        /// </summary>
        private bool HostAreaChanged(Rectangle area, Bitmap candidate)
        {
            if (_shotVisible == null || candidate == null) return true;
            Rectangle frame = new Rectangle(0, 0, _shotVisible.Width, _shotVisible.Height);
            Rectangle probe = Rectangle.Intersect(area, frame);
            if (probe.Width < 8 || probe.Height < 8) return true;
            int stepX = Math.Max(1, probe.Width / 12);
            int stepY = Math.Max(1, probe.Height / 12);
            int sampled = 0;
            int changed = 0;
            for (int x = probe.Left + stepX / 2; x < probe.Right; x += stepX)
            {
                for (int y = probe.Top + stepY / 2; y < probe.Bottom; y += stepY)
                {
                    sampled++;
                    try
                    {
                        if (_shotVisible.GetPixel(x, y) != candidate.GetPixel(x, y)) changed++;
                    }
                    catch (Exception) { }
                }
            }
            if (sampled == 0) return true;
            // A small share is enough: the window moved away, the desktop behind it looks different.
            return changed * 100 >= sampled * 8;
        }

        /// <summary>
        /// At open: hide the Harness window, wait until it is really gone from the screen, and freeze
        /// that clean frame. Shift later only swaps between the two frozen frames, so toggling costs
        /// no capture and never flashes �?at the price of a few hundred milliseconds before the
        /// overlay appears.
        /// </summary>
        private void PrepareHostVariants()
        {
            _shotVisible = _shot;
            if (HidePid <= 0 && string.IsNullOrEmpty(HideExe)) return;
            try
            {
                System.Collections.Generic.List<IntPtr> found = new System.Collections.Generic.List<IntPtr>();
                NativeApi.EnumWindows(delegate(IntPtr hwnd, IntPtr unused)
                {
                    uint pid;
                    NativeApi.GetWindowThreadProcessId(hwnd, out pid);
                    if (IsHostWindow(pid) && hwnd != Handle && NativeApi.IsWindowVisible(hwnd)) found.Add(hwnd);
                    return true;
                }, IntPtr.Zero);
                _hostWindows = found.ToArray();
                if (_hostWindows.Length == 0)
                {
                    FailHostHide("找不到 Harness 窗口（hide-pid=" + HidePid + "）");
                    return;
                }
                Rectangle hostArea = WindowFrame(_hostWindows[0]);
                for (int i = 0; i < _hostWindows.Length; i++) MinimizeWindow(_hostWindows[i]);
                NativeApi.DwmFlush();
                System.Threading.Thread.Sleep(150);

                bool anyMinimized = false;
                for (int i = 0; i < _hostWindows.Length; i++)
                {
                    if (NativeApi.IsIconic(_hostWindows[i])) { anyMinimized = true; break; }
                }
                if (!anyMinimized)
                {
                    // Hiding did not take effect; capturing now would hand back a picture of our own
                    // window, which the user would mistake for a desktop screenshot.
                        FailHostHide("无法最小化 Harness 窗口（最小化未生效）");
                    return;
                }

                // Adaptive settle: grab as soon as the window is gone from the frame instead of
                // always waiting a fixed 340 ms.
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    System.Threading.Thread.Sleep(attempt == 0 ? 110 : 90);
                    Bitmap candidate = ScreenGrab.Capture(Bounds);
                    if (candidate == null) break;
                    bool gone = HostAreaChanged(hostArea, candidate);
                    if (gone || attempt == 2) { _shotHidden = candidate; break; }
                    candidate.Dispose();
                }
                if (_shotHidden != null)
                {
                    _shot = _shotHidden;
                    _hostHidden = true;
                    HostPickable = false;
                }
                ShotApp.Log("open: hidden frame=" + (_shotHidden != null ? "1" : "0")
                    + " windows=" + _hostWindows.Length);
                    Invalidate();
                UpdateToggleButton();
            }
            catch (Exception ex) { ShotApp.Log("open: error " + ex.Message); }
        }

        /// <summary>
        /// Shift: really minimize or restore the Harness window, then grab the screen again.
        /// The overlay is made transparent for the grab so it never appears in the picture, and the
        /// fresh frame replaces the old one, so what the user sees is always what gets cropped.
        /// </summary>
        /// <summary>
        /// Shift: really minimize or restore the Harness window, then show the matching frozen
        /// frame. Both frames were grabbed while this overlay was still fully transparent, so the
        /// swap is instant and nothing flickers �?a live re-capture needs the overlay transparent
        /// for a moment, which the user sees as the screen blinking (observed 2026-10-19).
        /// </summary>
        /// <summary>
        /// Shift: hide or show the Harness window. The hidden frame is captured lazily on the first
        /// hide �?capturing it costs a minimise plus a settle, and doing that up front would put that
        /// wait on every capture. The overlay is transparent for that one grab so it never appears in
        /// the picture; afterwards switching back and forth is a plain bitmap swap.
        /// </summary>
        /// <summary>
        /// Shift: really hide or show the Harness window, then show the matching frozen frame. Both
        /// frames already exist (grabbed at open), so this is a plain bitmap swap �?no capture and
        /// nothing flickers.
        /// </summary>
        private void SetHostHidden(bool hidden)
        {
            try
            {
                if (hidden)
                {
                    for (int i = 0; i < _hostWindows.Length; i++) MinimizeWindow(_hostWindows[i]);
                }
                else
                {
                    ShowHostWindows();
                }
                _hostHidden = hidden;
                HostPickable = !hidden;
                Bitmap variant = hidden ? _shotHidden : _shotVisible;
                if (variant != null)
                {
                    _shot = variant;
                    _hover = Rectangle.Empty;
                    _hoverActive = true;
                    _hasSelection = false;
                    Invalidate();
                    _selection = Rectangle.Empty;
                }
                UpdateToggleButton();
                ShotApp.Log("toggle: host hidden=" + (hidden ? "1" : "0")
                    + " frame=" + (variant == null ? "none" : (variant == _shotHidden ? "hidden" : "visible")));
            }
            catch (Exception ex) { ShotApp.Log("toggle: error " + ex.Message); }
        }

        private void HideHostWindows()
        {
            try
            {
                System.Collections.Generic.List<IntPtr> found = new System.Collections.Generic.List<IntPtr>();
                NativeApi.EnumWindows(delegate(IntPtr hwnd, IntPtr unused)
                {
                    uint pid;
                    NativeApi.GetWindowThreadProcessId(hwnd, out pid);
                    if (IsHostWindow(pid) && hwnd != Handle && NativeApi.IsWindowVisible(hwnd)) found.Add(hwnd);
                    return true;
                }, IntPtr.Zero);
                _hostWindows = found.ToArray();
                for (int i = 0; i < _hostWindows.Length; i++)
                {
                    // Minimize rather than hide: the window leaves the screen either way, but a
                    // minimized window keeps its taskbar entry, which is what the user expects.
                    MinimizeWindow(_hostWindows[i]);
                    ShotApp.Log("autohide: hwnd=" + _hostWindows[i].ToInt64()
                        + " visibleAfter=" + (NativeApi.IsWindowVisible(_hostWindows[i]) ? "1" : "0"));
                }
                ShotApp.Log("autohide: minimized " + _hostWindows.Length + " window(s) of pid " + HidePid);
                if (_hostWindows.Length > 0)
                {
                    _hostHidden = true;
                    // Minimizing the foreground window hands focus to somebody else; take it back.
                    ForceForeground();
                    NativeApi.DwmFlush();
                    System.Threading.Thread.Sleep(320);
                    RecaptureShot();
                    UpdateToggleButton();
                }
            }
            catch (Exception ex) { ShotApp.Log("autohide: error " + ex.Message); }
        }

        /// <summary>Bring the Harness windows back, without stealing the overlay's foreground.</summary>
        private void ShowHostWindows()
        {
            for (int i = 0; i < _hostWindows.Length; i++)
            {
                RestoreWindow(_hostWindows[i]);
            }
            if (_hostWindows.Length > 0)
            {
                NativeApi.DwmFlush();
                // Re-assert the overlay: giving the Harness window the foreground put it on top and
                // every later click landed in the page instead of this overlay.
                try
                {
                    Activate();
                    BringToFront();
                    Focus();
                    NativeApi.SetForegroundWindow(Handle);
                }
                catch (Exception) { }
            }
        }


        /// <summary>Re-freeze the desktop after a window disappeared or came back.</summary>
        private void RecaptureShot()
        {
            // Our own chrome must not land in the frozen frame: fade the overlay to fully
            // transparent, let DWM commit that, capture, then restore. Hiding it instead would
            // drop the foreground and the next Shift would be lost.
            double priorOpacity = Opacity;
            Bitmap fresh = null;
            try
            {
                Opacity = 0;
                NativeApi.DwmFlush();
                System.Threading.Thread.Sleep(90);
                fresh = ScreenGrab.Capture(Bounds);
            }
            finally
            {
                Opacity = priorOpacity;
                NativeApi.DwmFlush();
            }
            if (fresh == null) return;
            Bitmap old = _shot;
            _shot = fresh;
            if (old != null && !ReferenceEquals(old, fresh) && !ReferenceEquals(old, _shotVisible) && !ReferenceEquals(old, _shotHidden)) old.Dispose();
            _hover = Rectangle.Empty;
            _hoverActive = true;
            _hasSelection = false;
            Invalidate();
            _selection = Rectangle.Empty;
        }

        /// <summary>Shift / the on-screen toggle: hide or keep the Harness window.</summary>
        /// <summary>
        /// One toggle, guarded here rather than at each caller. A single physical Shift press
        /// reaches this method twice (the low-level hook fires, and the queued key message is
        /// handled afterwards), and a toggle now takes longer than the old 200 ms debounce window
        /// because it re-captures the screen �?so the second call undid the first and the picture
        /// never changed (observed 2026-10-19).
        /// </summary>
        private void ToggleMode()
        {
            if (_toggling) return;
            int nowTicks = Environment.TickCount;
            if (_lastToggleTicks != 0 && nowTicks - _lastToggleTicks <= 150) return;
            _toggling = true;
            try
            {
                if (HidePid > 0 && _hostWindows.Length > 0)
                {
                    SetHostHidden(!_hostHidden);
                }
                else
                {
                    ToggleTargetWindow();
                }
            }
            finally
            {
                _toggling = false;
                _lastToggleTicks = Environment.TickCount;
            }
        }

        private float ScaleX()
        {
            if (ClientSize.Width < 1) return 1f;
            return (float)_shot.Width / ClientSize.Width;
        }

        private float ScaleY()
        {
            if (ClientSize.Height < 1) return 1f;
            return (float)_shot.Height / ClientSize.Height;
        }

        private Point ToImage(Point client)
        {
            int x = (int)Math.Round(client.X * ScaleX());
            int y = (int)Math.Round(client.Y * ScaleY());
            if (x < 0) x = 0;
            if (y < 0) y = 0;
            if (x > _shot.Width) x = _shot.Width;
            if (y > _shot.Height) y = _shot.Height;
            return new Point(x, y);
        }

        private Rectangle ToClientRect(Rectangle image)
        {
            float invX = ClientSize.Width < 1 ? 1f : (float)ClientSize.Width / _shot.Width;
            float invY = ClientSize.Height < 1 ? 1f : (float)ClientSize.Height / _shot.Height;
            return new Rectangle(
                (int)Math.Round(image.X * invX),
                (int)Math.Round(image.Y * invY),
                (int)Math.Round(image.Width * invX),
                (int)Math.Round(image.Height * invY));
        }

        private Bitmap _veilCache;
        private Bitmap _veilSource;

        // GDI mirror of the two source bitmaps. Blitting a DDB is a straight memcpy in gdi32,
        // while Graphics.DrawImage goes through the GDI+ pipeline (~0.5 ms per call even for a
        // same-size blit) �?at a few hundred frames per second during a drag that adds up.
        private IntPtr _veilDC;
        private IntPtr _veilDdb;
        private IntPtr _shotDC;
        private IntPtr _shotDdb;
        private Bitmap _shotDdbSource;

        /// <summary>
        /// Build �?once per captured frame �?a pre-composited "picture + veil" bitmap. Painting that
        /// with DrawImageUnscaled is a straight blit; compositing it on every mouse move would be
        /// slower than the thing this replaces.
        /// </summary>
        private void EnsureVeilCache()
        {
            if (_veilCache != null
                && ReferenceEquals(_veilSource, _shot)
                && _veilCache.Width == ClientSize.Width
                && _veilCache.Height == ClientSize.Height)
            {
                return;
            }
            if (_veilCache != null) { _veilCache.Dispose(); _veilCache = null; }
            _veilCache = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
            using (Graphics g = Graphics.FromImage(_veilCache))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                // 1:1 crop, never a resize: the window may be 2 px smaller than the capture (see
                // the Wallpaper Engine inset in OnLoad), and squeezing 2560 px into 2558 would
                // shift every selection edge.
                g.DrawImageUnscaled(_shot, 0, 0);
                using (SolidBrush veil = new SolidBrush(Color.FromArgb(130, 0, 0, 0)))
                {
                    g.FillRectangle(veil, 0, 0, _veilCache.Width, _veilCache.Height);
                }
            }
            _veilSource = _shot;
            RebuildVeilDdb();
        }

        /// <summary>Refresh the GDI bitmap mirror of _veilCache (called whenever the cache is rebuilt).</summary>
        private void RebuildVeilDdb()
        {
            if (_veilDdb != IntPtr.Zero)
            {
                NativeApi.SelectObject(_veilDC, IntPtr.Zero);
                NativeApi.DeleteObject(_veilDdb);
                _veilDdb = IntPtr.Zero;
            }
            if (_veilDC == IntPtr.Zero) _veilDC = NativeApi.CreateCompatibleDC(IntPtr.Zero);
            if (_veilCache != null && _veilDC != IntPtr.Zero)
            {
                _veilDdb = _veilCache.GetHbitmap();
                NativeApi.SelectObject(_veilDC, _veilDdb);
            }
        }

        /// <summary>Lazily mirror _shot (it swaps when Shift toggles the hidden-window variant).</summary>
        private void EnsureShotDdb()
        {
            if (ReferenceEquals(_shotDdbSource, _shot) && _shotDdb != IntPtr.Zero) return;
            if (_shotDdb != IntPtr.Zero)
            {
                NativeApi.SelectObject(_shotDC, IntPtr.Zero);
                NativeApi.DeleteObject(_shotDdb);
                _shotDdb = IntPtr.Zero;
            }
            if (_shotDC == IntPtr.Zero) _shotDC = NativeApi.CreateCompatibleDC(IntPtr.Zero);
            if (_shot != null && _shotDC != IntPtr.Zero)
            {
                _shotDdb = _shot.GetHbitmap();
                NativeApi.SelectObject(_shotDC, _shotDdb);
            }
            _shotDdbSource = _shot;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            // Blit the pre-composited "picture + veil" instead of scaling the 3.7 MP source on every
            // frame: DrawImage into a same-size dest rect still ran through the resampler, which is
            // what made the selection lag behind the cursor.
            EnsureVeilCache();
            // Repaint only the invalidated strip. Blitting the whole 3.7 MP cache forced a
            // full-surface upload on every mouse move, which capped dragging well below 60 fps.
            Rectangle clip = e.ClipRectangle;
            if (clip.Width <= 0 || clip.Height <= 0) clip = ClientRectangle;
            EnsureShotDdb();

            // A drag selection wins; otherwise the hovered window is highlighted, which is what
            // makes a single click able to capture a whole window (WeChat's behaviour).
            Rectangle highlight = _hasSelection ? _selection : _hover;
            bool hasHighlight = highlight.Width > 1 && highlight.Height > 1;
            Rectangle highlightClient = hasHighlight ? ToClientRect(highlight) : Rectangle.Empty;

            // gdi32 BitBlt instead of Graphics.DrawImage: the two backing images never need
            // resampling or alpha, so a raw blit is several times cheaper per frame.
            //
            // Fast path: when the dirty rect lies entirely inside the selection, the veil would be
            // blitted and then completely covered by the clear copy, so skip it. This is the common
            // case while dragging a large box. Anything mixed (the usual border-hugging rect) keeps
            // the fully-safe order: veil over the rect, clear copy on top.
            IntPtr paintDc = g.GetHdc();
            try
            {
                Rectangle full = Rectangle.Intersect(clip, ClientRectangle);
                if (full.Width > 0 && full.Height > 0)
                {
                    bool insideOnly = hasHighlight && highlightClient.Contains(full);
                    if (!insideOnly)
                    {
                        NativeApi.BitBlt(paintDc, full.X, full.Y, full.Width, full.Height, _veilDC, full.X, full.Y, NativeApi.SRCCOPY);
                    }
                    if (hasHighlight)
                    {
                        Rectangle keep = Rectangle.Intersect(full, highlightClient);
                        if (keep.Width > 0 && keep.Height > 0)
                        {
                            Rectangle src = new Rectangle(
                                highlight.X + (keep.X - highlightClient.X),
                                highlight.Y + (keep.Y - highlightClient.Y),
                                keep.Width,
                                keep.Height);
                            NativeApi.BitBlt(paintDc, keep.X, keep.Y, keep.Width, keep.Height, _shotDC, src.X, src.Y, NativeApi.SRCCOPY);
                        }
                    }
                }
            }
            finally
            {
                g.ReleaseHdc(paintDc);
            }
            _paintClip = clip;

            if (hasHighlight)
            {
                Rectangle r = highlightClient;

                float accent = Math.Max(1f, 2f * _scale);
                using (Pen border = new Pen(Color.FromArgb(255, 51, 200, 180), accent))
                {
                    g.DrawRectangle(border, r.X, r.Y, r.Width, r.Height);
                }
                // Thin inner hairline: gives the frame an edge on both light and dark content.
                using (Pen inner = new Pen(Color.FromArgb(70, 255, 255, 255), 1f))
                {
                    g.DrawRectangle(inner, r.X + accent, r.Y + accent, r.Width - accent * 2, r.Height - accent * 2);
                }
                // Corner handles: filled squares, half outside the frame, so they read as grab points
                // instead of blending into the border.
                int handle = (int)Math.Round(6 * _scale);
                int half = handle / 2;
                using (SolidBrush dot = new SolidBrush(Color.FromArgb(255, 231, 243, 246)))
                using (Pen dotEdge = new Pen(Color.FromArgb(255, 51, 200, 180), 1f))
                {
                    Point[] corners = new Point[]
                    {
                        new Point(r.Left, r.Top), new Point(r.Right, r.Top),
                        new Point(r.Left, r.Bottom), new Point(r.Right, r.Bottom),
                    };
                    for (int k = 0; k < corners.Length; k++)
                    {
                        Rectangle dotRect = new Rectangle(corners[k].X - half, corners[k].Y - half, handle, handle);
                        g.FillRectangle(dot, dotRect);
                        g.DrawRectangle(dotEdge, dotRect);
                    }
                }

                if (_dragging) DrawSizeLabel(g, highlight); else _sizeLabelRect = Rectangle.Empty;
                DrawTickButton(g);
            }

            string hint;
            if (_hasSelection)
            {
                hint = "\u62d6\u52a8\u91cd\u9009  \u00b7  Enter \u5b8c\u6210  \u00b7  \u53f3\u952e\u53d6\u6d88";
            }
            else if (hasHighlight)
            {
                hint = "\u5355\u51fb\u622a\u53d6\u8be5\u7a97\u53e3  \u00b7  \u62d6\u52a8\u6846\u9009  \u00b7  Esc \u9000\u51fa";
            }
            else
            {
                hint = "\u5fae\u4fe1\u5f0f\u622a\u56fe  \u00b7  \u60ac\u505c\u9009\u4e2d\u7a97\u53e3  \u00b7  \u62d6\u52a8\u6846\u9009  \u00b7  Esc \u9000\u51fa";
            }
            if (_hostWindows.Length > 0 || TargetIsHideable())
            {
                hint = hint + "  \u00b7  Shift \u9690\u85cf/\u4fdd\u7559";
            }
            if (_hidForeground)
            {
                hint = hint + "  \u00b7  \u9690\u85cf\u7a97\u53e3\u7248\uff08\u518d\u6309 Shift \u5207\u56de\uff09";
            }
            if (hasHighlight && !_dragging)
            {
                // No floating label when idle: the size belongs to the pill so nothing can be
                // left behind on screen once the drag ends.
                hint = hint + "  \u00b7  " + highlight.Width.ToString() + " \u00d7 " + highlight.Height.ToString();
            }
            DrawPill(g, hint, _hintFont);
        }


        private static readonly uint OwnProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

        /// <summary>
        /// Follow the window under the cursor so it can be captured with a single click.
        /// Mirrors WeChat: the highlight tracks the top-level window, not the cursor, and it
        /// uses the DWM visible frame so the rectangle hugs exactly what the user sees.
        /// </summary>
        private void PollHover()
        {
            if (!_hoverActive || _dragging) return;

            Point cursor = Cursor.Position;
            // In the hidden variant the frozen picture is "the live screen minus the target
            // window", so detection must ignore that window too �?then the window the cursor is
            // over is exactly the one the user sees in the picture.
            IntPtr hit = FindWindowAt(cursor, Handle, _windowHidden ? _targetWindow : IntPtr.Zero);

            if (hit != _lastHoverHwnd)
            {
                _lastHoverHwnd = hit;
                uint hoverPid = 0;
                if (hit != IntPtr.Zero) NativeApi.GetWindowThreadProcessId(hit, out hoverPid);
                Rectangle hf = hit == IntPtr.Zero ? Rectangle.Empty : WindowFrame(hit);
                string hoverExe = hoverPid == 0 ? "" : ImagePathOf(hoverPid);
                ShotApp.Log("hover: cursor=" + cursor.X + "," + cursor.Y
                    + " hwnd=0x" + hit.ToInt64().ToString("X") + " pid=" + hoverPid
                    + " cls=" + (hit == IntPtr.Zero ? "" : Desktop.ClassOf(hit))
                    + " exe=" + (hoverExe ?? "")
                    + " win=" + hf.X + "," + hf.Y + " " + hf.Width + "x" + hf.Height
                    + " hostMode=" + (_hostWindows.Length > 0 ? "1" : "0")
                    + " hidden=" + (_hostHidden ? "1" : "0"));
            }

            Rectangle local = Rectangle.Empty;
            if (hit == IntPtr.Zero)
            {
                // Nothing real under the cursor: the desktop, the taskbar, or one of the shell
                // surfaces we deliberately skip. Over the desktop, highlight the working area so a
                // click captures it without the taskbar. Over the taskbar itself, highlight the
                // whole screen instead �?that is the only way to get the taskbar into a click
                // capture once the shell taskbar window is filtered out of the candidate list.
                Screen scr = Screen.FromPoint(cursor);
                Rectangle work = scr.WorkingArea;
                if (work.Contains(cursor))
                {
                    local = new Rectangle(work.X - Bounds.X, work.Y - Bounds.Y, work.Width, work.Height);
                    local.Intersect(new Rectangle(0, 0, _shot.Width, _shot.Height));
                    if (local.Width < 8 || local.Height < 8)
                    {
                        local = new Rectangle(0, 0, _shot.Width, _shot.Height);
                    }
                }
                else
                {
                    local = new Rectangle(0, 0, _shot.Width, _shot.Height);
                }
            }
            else
            {
                Rectangle found = WindowFrame(hit);
                Rectangle screen = Bounds;
                local = new Rectangle(found.X - screen.X, found.Y - screen.Y, found.Width, found.Height);
                local.Intersect(new Rectangle(0, 0, _shot.Width, _shot.Height));
                if (local.Width < 8 || local.Height < 8) local = Rectangle.Empty;
            }

            if (local != _hover)
            {
                Rectangle before = _hover;
                _hover = local;
                InvalidateHighlightChange(before, local);
            }
        }

        /// <summary>
        /// The topmost window containing a screen point, skipping our own capture overlay.
        /// Shared by the live hover timer and the headless --windowat probe so the tested code
        /// path is exactly the shipped one.
        /// </summary>
        internal static IntPtr FindWindowAt(Point cursor, IntPtr skipHandle, IntPtr skipAlso)
        {
            IntPtr hit = IntPtr.Zero;
            NativeApi.EnumWindows(delegate(IntPtr hwnd, IntPtr unused)
            {
                if (hwnd == skipHandle) return true;
                if (skipAlso != IntPtr.Zero && hwnd == skipAlso) return true;
                if (!IsCandidate(hwnd)) return true;
                Rectangle frame = WindowFrame(hwnd);
                if (frame.Width < 40 || frame.Height < 40) return true;
                if (!frame.Contains(cursor)) return true;
                hit = hwnd;
                return false; // first match in Z order wins
            }, IntPtr.Zero);
            return hit;
        }

        private static bool IsCandidate(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            if (!NativeApi.IsWindowVisible(hwnd)) return false;
            if (NativeApi.IsIconic(hwnd)) return false;
            uint ownerPid;
            NativeApi.GetWindowThreadProcessId(hwnd, out ownerPid);
            if (ownerPid == OwnProcessId) return false;
            // Skip the Harness window while it is hidden (clicking it would mean "capture my own
            // frozen picture"). Once Shift brings it back it is an ordinary window and may be
            // captured like any other.
            if (!HostPickable && IsHostWindow(ownerPid)) return false;
            string cls = Desktop.ClassOf(hwnd);
            // Shell surfaces are transparent and often cover the whole screen, so they win the
            // Z-order hit test and highlight everything (observed 2026-10-19: hovering the taskbar
            // picked TextInputHost.exe's full-screen Windows.UI.Core.CoreWindow).
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd"
                || cls == "Windows.UI.Core.CoreWindow" || cls == "DesktopWallpaperManager"
                || cls == "EdgeUiInputTopWndClass" || cls == "XamlExplorerHostIslandWindow"
                || cls == "TopLevelWindowForOverflowXamlIsland")
            {
                return false;
            }
            // The IME host draws an invisible full-screen surface over everything; never a target.
            string ownerExe = ImagePathOf(ownerPid);
            if (ownerExe != null && ownerExe.EndsWith("TextInputHost.exe", StringComparison.OrdinalIgnoreCase)) return false;
            // Wallpaper Engine's wallpaper window is a full-screen Chrome_WidgetWin_1 that it
            // tears down and recreates whenever a full-screen app appears. Hovering the desktop
            // then alternated between that window (0,0 2560x1440, taskbar included) and the shell
            // window underneath (10,0 2540x1370, taskbar excluded) �?the 1-2 frame flicker.
            if (ownerExe != null && ownerExe.IndexOf("\\wallpaper_engine\\", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            // Explorer's ApplicationFrameWindow is the desktop shell surface, not an app window.
            if (cls == "ApplicationFrameWindow" && ownerExe != null
                && ownerExe.EndsWith("\\explorer.exe", StringComparison.OrdinalIgnoreCase)) return false;
            NativeApi.RECT cloaked;
            int cloakedHr = NativeApi.DwmGetWindowAttribute(hwnd, NativeApi.DWMWA_CLOAKED, out cloaked, Marshal.SizeOf(typeof(NativeApi.RECT)));
            if (cloakedHr == 0 && cloaked.Right != 0) return false;
            NativeApi.RECT frame;
            if (!WindowFrame(hwnd, out frame)) return false;
            return (frame.Right - frame.Left) >= 40 && (frame.Bottom - frame.Top) >= 40;
        }

        private static bool WindowFrame(IntPtr hwnd, out NativeApi.RECT frame)
        {
            NativeApi.RECT extended;
            int hr = NativeApi.DwmGetWindowAttribute(hwnd, NativeApi.DWMWA_EXTENDED_FRAME_BOUNDS, out extended, Marshal.SizeOf(typeof(NativeApi.RECT)));
            if (hr == 0 && (extended.Right - extended.Left) > 0 && (extended.Bottom - extended.Top) > 0)
            {
                frame = extended;
                return true;
            }
            return NativeApi.GetWindowRect(hwnd, out frame);
        }

        private static Rectangle WindowFrame(IntPtr hwnd)
        {
            NativeApi.RECT frame;
            if (!WindowFrame(hwnd, out frame)) return Rectangle.Empty;
            return new Rectangle(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
        }

        /// <summary>
        /// Where the "W x H" readout would sit. Split out from the drawing code on purpose: while a
        /// drag is in progress only the changed strips are repainted, so the caller has to know the
        /// label's previous box and invalidate it too. Otherwise every pointer move leaves the old
        /// label behind and a drag paints a trail of them.
        /// </summary>
        private Rectangle SizeLabelBox(Rectangle highlight)
        {
            string size = highlight.Width.ToString() + " \u00d7 " + highlight.Height.ToString();
            Size textSize = TextRenderer.MeasureText(size, _sizeFont);
            int padX = (int)Math.Round(9 * _scale);
            int padY = (int)Math.Round(4 * _scale);
            int w = textSize.Width + padX * 2;
            int h = textSize.Height + padY * 2;
            int gap = (int)Math.Round(14 * _scale);
            Point cursor = Cursor.Position;
            int x = cursor.X + gap;
            int y = cursor.Y + gap;
            if (x + w > ClientSize.Width - gap) x = cursor.X - gap - w;
            if (y + h > ClientSize.Height - gap) y = cursor.Y - gap - h;
            if (x < gap) x = gap;
            if (y < gap) y = gap;
            // Keep it clear of the frame's top edge when the pointer sits just above the selection.
            Rectangle client = ToClientRect(highlight);
            if (y + h > client.Top && y < client.Top && x + w > client.Left && x < client.Right) y = client.Top + h + gap;
            return new Rectangle(x, y, w, h);
        }

        /// <summary>
        /// The "W x H" readout shown while dragging. It is only drawn during a drag: the overlay
        /// repaints on selection changes, not on every pointer move, so a label drawn after release
        /// would sit frozen wherever it was last painted. When idle the size lives in the hint pill.
        /// </summary>
        private void DrawSizeLabel(Graphics g, Rectangle highlight)
        {
            string size = highlight.Width.ToString() + " \u00d7 " + highlight.Height.ToString();
            Rectangle box = SizeLabelBox(highlight);
            _sizeLabelRect = box;
            if (!_paintClip.IsEmpty && !_paintClip.IntersectsWith(box)) return;
            int padX = (int)Math.Round(9 * _scale);
            int padY = (int)Math.Round(4 * _scale);
            int radius = Math.Min(box.Height, (int)Math.Round(7 * _scale));
            using (GraphicsPath path = RoundRect(box, radius))
            {
                using (SolidBrush back = new SolidBrush(Color.FromArgb(235, 15, 21, 24)))
                {
                    g.FillPath(back, path);
                }
                using (Pen edge = new Pen(Color.FromArgb(41, 130, 230, 215), 1f))
                {
                    g.DrawPath(edge, path);
                }
            }
            TextRenderer.DrawText(g, size, _sizeFont, new Point(box.X + padX, box.Y + padY),
                Color.FromArgb(231, 243, 246), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        /// <summary>Rounded rectangle path, shared by the size label and the hint pill.</summary>
        private static GraphicsPath RoundRect(Rectangle box, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = Math.Max(1, radius * 2);
            if (d > box.Height) d = box.Height;
            if (d > box.Width) d = box.Width;
            path.AddArc(box.Left, box.Top, d, d, 180, 90);
            path.AddArc(box.Right - d, box.Top, d, d, 270, 90);
            path.AddArc(box.Right - d, box.Bottom - d, d, d, 0, 90);
            path.AddArc(box.Left, box.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void DrawPill(Graphics g, string text, Font font)
        {
            Size textSize = TextRenderer.MeasureText(g, text, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            int padX = (int)Math.Round(16 * _scale);
            int padY = (int)Math.Round(8 * _scale);
            int w = textSize.Width + padX * 2;
            int h = textSize.Height + padY * 2;
            int x = (ClientSize.Width - w) / 2;
            int y = (int)Math.Round(20 * _scale);
            if (x < 0) x = 0;
            Rectangle box = new Rectangle(x, y, w, h);
            if (!_paintClip.IsEmpty && !_paintClip.IntersectsWith(box)) return;
            using (GraphicsPath path = RoundRect(box, h / 2))
            {
                using (SolidBrush back = new SolidBrush(Color.FromArgb(235, 15, 21, 24)))
                {
                    g.FillPath(back, path);
                }
                using (Pen edge = new Pen(Color.FromArgb(41, 130, 230, 215), 1f))
                {
                    g.DrawPath(edge, path);
                }
            }
            TextRenderer.DrawText(g, text, font, new Point(x + padX, y + padY), Color.FromArgb(231, 243, 246), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right)
            {
                // Cancel on mouse UP, not down: closing here would let the matching button-up fall
                // through to whatever window is underneath, and that window would run its own
                // right-click action (a context menu) �?most visible at the screen edges.
                _rightDown = true;
                Capture = true;
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (_okRect.Width > 0 && _okRect.Contains(e.Location)) { Accept(_selection); return; }
            // Remember what was highlighted so a click without dragging can take the whole
            // window (WeChat: hover a window, click once, done).
            _pressedHover = _hover;
            _pressPoint = e.Location;
            _hoverActive = false;
            // Repaint only what the drag start actually changes: the highlight that disappears,
            // the tick that goes away, and the pill whose text switches to "drag" mode. A full
            // Invalidate here repainted the whole 3.7 MP backdrop and showed up as a hitch at
            // the exact moment the drag began.
            Rectangle oldHover = _hover;
            Rectangle oldSelection = _selection;
            Rectangle oldTick = _okRect;
            _hover = Rectangle.Empty;
            _hoverTimer.Stop();
            _dragging = true;
            _start = ToImage(e.Location);
            _hasSelection = false;
            _selection = Rectangle.Empty;
            _okRect = Rectangle.Empty;
            Rectangle dirty = Rectangle.Empty;
            if (oldHover.Width > 1 && oldHover.Height > 1) dirty = Rectangle.Union(dirty, Rectangle.Inflate(ToClientRect(oldHover), 12, 12));
            if (oldSelection.Width > 1 && oldSelection.Height > 1) dirty = Rectangle.Union(dirty, Rectangle.Inflate(ToClientRect(oldSelection), 12, 12));
            if (oldTick.Width > 0) dirty = Rectangle.Union(dirty, Rectangle.Inflate(oldTick, 6, 6));
            dirty = Rectangle.Union(dirty, new Rectangle(0, 0, ClientSize.Width, (int)Math.Max(48, 84 * _scale)));
            dirty.Intersect(ClientRectangle);
            if (!dirty.IsEmpty) Invalidate(dirty); else Invalidate();
        }

        /// <summary>
        /// Repaint only the pixels that actually change when the highlight moves: the old rectangle,
        /// the new one, plus a margin for the border and the size label, and the hint strip whose
        /// text depends on the state. Invalidating the whole form made every mouse move blit the
        /// full 3.7 MP cache, which is what still felt sluggish while dragging.
        /// </summary>
        private void InvalidateHighlightChange(Rectangle before, Rectangle after)
        {
            int margin = (int)Math.Max(8, 14 * _scale);
            Rectangle a = before.Width > 1 && before.Height > 1 ? ToClientRect(before) : Rectangle.Empty;
            Rectangle b = after.Width > 1 && after.Height > 1 ? ToClientRect(after) : Rectangle.Empty;
            if (!a.IsEmpty) a.Inflate(margin, margin);
            if (!b.IsEmpty) b.Inflate(margin, margin);

            Rectangle dirty;
            if (a.IsEmpty) dirty = b;
            else if (b.IsEmpty) dirty = a;
            else dirty = Rectangle.Union(a, b);

            if (dirty.IsEmpty)
            {
                return;
            }
            dirty.Intersect(ClientRectangle);
            if (!dirty.IsEmpty) Invalidate(dirty);

            // The pill hint is centred at the top and its text only changes when the highlight
            // appears or disappears, so repaint that strip on those transitions only.
            bool stateChanged = (before.Width > 1 && before.Height > 1) != (after.Width > 1 && after.Height > 1);
            if (stateChanged) Invalidate(new Rectangle(0, 0, ClientSize.Width, (int)Math.Max(48, 84 * _scale)));
        }

        private static void AddDifferenceStrips(System.Collections.Generic.List<Rectangle> into, Rectangle outer, Rectangle inner)
        {
            if (!outer.IntersectsWith(inner))
            {
                into.Add(outer);
                return;
            }
            int top = inner.Top - outer.Top;
            if (top > 0) into.Add(new Rectangle(outer.X, outer.Y, outer.Width, top));
            int bottom = outer.Bottom - inner.Bottom;
            if (bottom > 0) into.Add(new Rectangle(outer.X, outer.Bottom - bottom, outer.Width, bottom));
            int midTop = Math.Max(outer.Top, inner.Top);
            int midBottom = Math.Min(outer.Bottom, inner.Bottom);
            int midHeight = midBottom - midTop;
            if (midHeight <= 0) return;
            int left = inner.Left - outer.Left;
            if (left > 0) into.Add(new Rectangle(outer.Left, midTop, left, midHeight));
            int right = outer.Right - inner.Right;
            if (right > 0) into.Add(new Rectangle(outer.Right - right, midTop, right, midHeight));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging)
            {
                // Hand cursor and a brighter circle while the pointer is over the tick.
                bool overTick = _okRect.Width > 0 && _okRect.Contains(e.Location);
                if (overTick != _okHover)
                {
                    _okHover = overTick;
                    Cursor = overTick ? Cursors.Hand : Cursors.Cross;
                    if (_okRect.Width > 0) Invalidate(Rectangle.Inflate(_okRect, 4, 4));
                }
                return;
            }
            ApplyDragPoint(ToImage(e.Location));
        }

        private void ApplyDragPoint(Point current)
        {
            int x = Math.Min(_start.X, current.X);
            int y = Math.Min(_start.Y, current.Y);
            int w = Math.Abs(current.X - _start.X);
            int h = Math.Abs(current.Y - _start.Y);
            Rectangle before = _selection;
            _selection = new Rectangle(x, y, w, h);
            _hasSelection = w >= 2 && h >= 2;
            InvalidateHighlightChange(before, _selection);
            Rectangle labelBefore = _sizeLabelRect;
            Rectangle labelNow = SizeLabelBox(_selection);
            Rectangle labelDirty = Rectangle.Union(labelBefore, labelNow);
            if (labelDirty.Width > 0 && labelDirty.Height > 0) Invalidate(Rectangle.Inflate(labelDirty, 6, 6));
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right)
            {
                if (_rightDown)
                {
                    _rightDown = false;
                    Capture = false;
                    Cancel();
                }
                return;
            }
            if (!_dragging) return;
            _dragging = false;
            // Drop the drag-time label and repaint the strip it last occupied.
            Rectangle staleLabel = _sizeLabelRect;
            _sizeLabelRect = Rectangle.Empty;
            if (staleLabel.Width > 0 && staleLabel.Height > 0) Invalidate(Rectangle.Inflate(staleLabel, 6, 6));

            int movedX = Math.Abs(e.Location.X - _pressPoint.X);
            int movedY = Math.Abs(e.Location.Y - _pressPoint.Y);
            bool clicked = movedX <= 4 && movedY <= 4;

            if (clicked && _pressedHover.Width > 1 && _pressedHover.Height > 1)
            {
                // A bare click on a highlighted window captures that window.
                Rectangle target = _pressedHover;
                _pressedHover = Rectangle.Empty;
                Accept(target);
                return;
            }

            _pressedHover = Rectangle.Empty;
            Rectangle beforeRelease = _selection;
            if (_selection.Width < 8 || _selection.Height < 8)
            {
                _hasSelection = false;
                _selection = Rectangle.Empty;
            }
            PositionButtons();
            // Repaint only what the release changes (final frame, tick, pill). A full Invalidate
            // here re-blitted the whole backdrop and made the drag end with a visible hitch too.
            Rectangle releaseDirty = Rectangle.Empty;
            if (beforeRelease.Width > 1 && beforeRelease.Height > 1)
                releaseDirty = Rectangle.Union(releaseDirty, Rectangle.Inflate(ToClientRect(beforeRelease), 12, 12));
            if (_selection.Width > 1 && _selection.Height > 1)
                releaseDirty = Rectangle.Union(releaseDirty, Rectangle.Inflate(ToClientRect(_selection), 12, 12));
            if (_okRect.Width > 0)
                releaseDirty = Rectangle.Union(releaseDirty, Rectangle.Inflate(_okRect, 8, 8));
            releaseDirty = Rectangle.Union(releaseDirty, new Rectangle(0, 0, ClientSize.Width, (int)Math.Max(48, 84 * _scale)));
            releaseDirty.Intersect(ClientRectangle);
            if (!releaseDirty.IsEmpty) Invalidate(releaseDirty); else Invalidate();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left) return;
            if (_okRect.Width > 0 && _okRect.Contains(e.Location)) { Accept(_selection); return; }
            if (!_hasSelection) return;
            Point image = ToImage(e.Location);
            if (_selection.Contains(image)) Accept(_selection);
        }

        /// <summary>
        /// Where the tick button sits. It is painted by OnPaint instead of being a WinForms control:
        /// a control's Region is ignored when the overlay renders itself to a bitmap, so a real
        /// button looked square in every snapshot. A painted one looks the same everywhere.
        /// </summary>
        private void PositionButtons()
        {
            if (!_hasSelection)
            {
                _okRect = Rectangle.Empty;
                return;
            }
            Rectangle r = ToClientRect(_selection);
            int gap = (int)Math.Round(10 * _scale);
            int d = (int)Math.Round(44 * _scale);
            // Just outside the selection's bottom-right corner, flipped inside when there is no room
            // below. Nothing sits next to it: cancelling is the right mouse button.
            int x = r.Right - d;
            int y = r.Bottom + gap;
            if (y + d > ClientSize.Height - gap) y = r.Bottom - d - gap;
            if (y < gap) y = gap;
            if (x < gap) x = gap;
            if (x + d > ClientSize.Width - gap) x = ClientSize.Width - gap - d;
            _okRect = new Rectangle(x, y, d, d);
        }

        /// <summary>The round tick: filled circle plus a white check, brightened on hover.</summary>
        private void DrawTickButton(Graphics g)
        {
            if (_okRect.Width <= 0) return;
            if (!_paintClip.IsEmpty && !_paintClip.IntersectsWith(_okRect)) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle box = _okRect;
            using (SolidBrush fill = new SolidBrush(_okHover ? Color.FromArgb(255, 105, 218, 202) : Color.FromArgb(255, 51, 200, 180)))
            {
                g.FillEllipse(fill, box);
            }
            using (Pen tick = new Pen(Color.FromArgb(255, 10, 20, 22), Math.Max(2f, 3.2f * _scale)))
            {
                tick.StartCap = LineCap.Round;
                tick.EndCap = LineCap.Round;
                tick.LineJoin = LineJoin.Round;
                g.DrawLines(tick, new PointF[]
                {
                    new PointF(box.Left + box.Width * 0.30f, box.Top + box.Height * 0.52f),
                    new PointF(box.Left + box.Width * 0.44f, box.Top + box.Height * 0.67f),
                    new PointF(box.Left + box.Width * 0.72f, box.Top + box.Height * 0.35f),
                });
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                Cancel();
                return true;
            }
            if (keyData == Keys.Enter)
            {
                Accept(_hasSelection ? _selection : _hover);
                return true;
            }
            if (keyData == Keys.ShiftKey)
            {
                // The overlay owns the keyboard, so a bare Shift press is delivered here even
                // though the browser never sees it. Same action as the on-screen toggle.
                ShotApp.Log("shift: received -> toggling");
                ToggleMode();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                Cancel();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                Accept(_hasSelection ? _selection : _hover);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.ShiftKey)
            {
                ToggleMode();
                e.Handled = true;
            }
        }

        private void Accept(Rectangle region)
        {
            Rectangle r = region;
            r.Intersect(new Rectangle(0, 0, _shot.Width, _shot.Height));
            if (r.Width < 1 || r.Height < 1) return;
            try
            {
                Result = _shot.Clone(r, PixelFormat.Format32bppArgb);
            }
            catch (Exception)
            {
                Result = null;
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        private void Cancel()
        {
            Result = null;
            DialogResult = DialogResult.Cancel;
            Close();
        }


        /// <summary>Set once the host window has been brought back before the overlay disappears.</summary>
        private bool _hostRestoredBeforeClose;

        /// <summary>
        /// Bring the hidden host window back while the overlay still covers the screen, then give
        /// the compositor a few frames to paint it. Restoring in OnFormClosed (the old behaviour)
        /// meant the overlay was already destroyed, so the desktop wallpaper showed for a frame or
        /// two before the Harness window reappeared.
        /// </summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_hostRestoredBeforeClose)
            {
                _hostRestoredBeforeClose = true;
                _hoverTimer.Stop();
                try
                {
                    if (HidePid > 0 && _hostWindows.Length > 0) ShowHostWindows();
                    if (_windowHidden && _targetWindow != IntPtr.Zero)
                    {
                        NativeApi.ShowWindow(_targetWindow, NativeApi.SW_SHOW);
                        if (NativeApi.IsIconic(_targetWindow)) NativeApi.ShowWindow(_targetWindow, NativeApi.SW_RESTORE);
                        NativeApi.SetForegroundWindow(_targetWindow);
                        _windowHidden = false;
                    }
                    NativeApi.DwmFlush();
                    // Two compositions: the host window is behind the overlay, which still hides it.
                    Thread.Sleep(80);
                    NativeApi.DwmFlush();
                }
                catch (Exception)
                {
                }
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            RemoveShiftHook();
            if (!_hostRestoredBeforeClose)
            {
                if (HidePid > 0 && _hostWindows.Length > 0) ShowHostWindows();
                // Whoever hid the front window (this overlay's toggle or the capture entry point),
                // it must come back on every close path: accept, cancel, Esc, or right-click.
                if (_windowHidden && _targetWindow != IntPtr.Zero)
                {
                    NativeApi.ShowWindow(_targetWindow, NativeApi.SW_SHOW);
                    if (NativeApi.IsIconic(_targetWindow)) NativeApi.ShowWindow(_targetWindow, NativeApi.SW_RESTORE);
                    NativeApi.DwmFlush();
                    NativeApi.SetForegroundWindow(_targetWindow);
                    _windowHidden = false;
                }
            }
            _hoverTimer.Stop();
            _hoverTimer.Dispose();
            if (_shotVisible != null) { _shotVisible.Dispose(); _shotVisible = null; }
            if (_shotHidden != null) { _shotHidden.Dispose(); _shotHidden = null; }
            if (_shot != null) { _shot.Dispose(); _shot = null; }
            _hintFont.Dispose();
            _sizeFont.Dispose();
        }
    }

    /// <summary>
    /// A hidden message-only window. It exists solely so UI work can be marshalled onto the
    /// app's own thread (BeginInvoke). It deliberately registers NO global hotkey: the tool is
    /// driven by the in-chat scissors button, and stealing a system-wide shortcut would be a
    /// side effect nobody asked for.
    /// </summary>
    /// <summary>
    /// Static helpers shared by the overlay and the inline capture path.
    ///
    /// The original standalone tray application is gone on purpose: the plugin always launches
    /// the engine as `--trigger`, so the tray icon, its context menu, the single-instance mutex
    /// and the config.ini round-trip had no caller here (removed 2026-10-19, ~450 lines).
    /// </summary>
    internal static class ShotApp
    {
        private static string LocalDir()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeChatShot");
        }

        internal static string ShotDir()
        {
            string dir = LocalDir();
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// Writable data directory: %LOCALAPPDATA%\WeChatShot, falling back to the exe's own
        /// folder when that location is denied (sandboxed launcher, roaming profile lock).
        /// </summary>
        internal static string DataDir()
        {
            // Prefer the folder the executable sits in: portable, always writable by whoever runs it,
            // and keeps the log next to the code that wrote it. Fall back to %LOCALAPPDATA% only when
            // that folder is read-only.
            try
            {
                string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
                if (!string.IsNullOrEmpty(exeDir))
                {
                    string probe = Path.Combine(exeDir, ".write-probe");
                    File.WriteAllText(probe, "1");
                    File.Delete(probe);
                    return exeDir;
                }
            }
            catch (Exception)
            {
            }
            try
            {
                string dir = LocalDir();
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch (Exception)
            {
            }
            return null;
        }


        /// <summary>
        /// Save a PNG with a fallback path. `Bitmap.Save(path)` can throw
        /// "GDI+ 娑擃厼褰傞悽鐔剁閼割剚鈧囨晩�?(0x80004005)" when the process's temp directory is not writable
        /// (observed under a sandboxed launcher). The fallback encodes into memory first and writes
        /// the bytes itself, which needs no GDI+ temp file.
        /// </summary>
        internal static bool SavePng(Bitmap bmp, string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string parent = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent)) Directory.CreateDirectory(parent);
                bmp.Save(full, ImageFormat.Png);
                return true;
            }
            catch (Exception first)
            {
                try
                {
                    using (MemoryStream ms = new MemoryStream())
                    {
                        bmp.Save(ms, ImageFormat.Png);
                        using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                        {
                            byte[] bytes = ms.ToArray();
                            fs.Write(bytes, 0, bytes.Length);
                        }
                    }
                    Log("save: direct write failed (" + first.Message + "), stream fallback ok");
                    return true;
                }
                catch (Exception second)
                {
                    Log("save: FAILED for " + path + " �?" + first.Message + " | " + second.Message);
                    return false;
                }
            }
        }

        internal static void Log(string line)
        {
            string dir = DataDir();
            if (dir == null) return;
            try
            {
                using (StreamWriter w = new StreamWriter(Path.Combine(dir, "wechatshot.log"), true, System.Text.Encoding.UTF8))
                {
                    w.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line);
                }
            }
            catch (Exception)
            {
            }
        }

        internal static bool CopyImage(Bitmap bmp)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    DataObject data = new DataObject();
                    using (MemoryStream ms = new MemoryStream())
                    {
                        bmp.Save(ms, ImageFormat.Png);
                        byte[] pngBytes = ms.ToArray();
                        data.SetData("PNG", false, pngBytes);
                    }
                    data.SetData(DataFormats.Bitmap, true, bmp);
                    // Deliberately image-only: adding CF_HDROP as well makes Chrome expose the
                    // same screenshot twice (once as an image, once as a file) and the composer
                    // would attach two copies of it.
                    Clipboard.SetDataObject(data, true, 6, 120);
                    return true;
                }
                catch (Exception ex)
                {
                    Log("clipboard attempt " + attempt.ToString() + " failed: " + ex.Message);
                    Thread.Sleep(140);
                }
            }
            return false;
        }
    }

    internal static class Program
    {
        /// <summary>Process start, used only to report how long opening the overlay took.</summary>
        internal static readonly DateTime Started = DateTime.Now;

        [STAThread]
        private static void Main(string[] args)
        {
            bool help = false;
            bool trigger = false;
            string outPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (string.Equals(a, "--trigger", StringComparison.OrdinalIgnoreCase)) trigger = true;
                else if (string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase)) help = true;
                else if (string.Equals(a, "--out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    outPath = args[i + 1];
                    i++;
                }
                else if (string.Equals(a, "--hide-pid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    try { ShotForm.HidePid = int.Parse(args[i + 1]); } catch (Exception) { ShotForm.HidePid = 0; }
                    i++;
                }
                else if (string.Equals(a, "--hide-exe", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    ShotForm.HideExe = args[i + 1];
                    i++;
                }
            }


            if (help)
            {
                Out("WeChatShot");
                Out("  --trigger          one interactive capture; 0 = captured, 2 = cancelled");
                Out("  --out <path>       with --trigger: also save the region to <path>");
                Out("  --hide-pid <pid>   hide this process's windows while capturing");
                Out("  --hide-exe <exe>   identify the host window by executable path");
                return;
            }

            Application.EnableVisualStyles();

            if (trigger)
            {
                // Always capture inline. The caller (the in-chat scissors button) waits for this
                // process, so the overlay lives exactly as long as that wait: no background tray
                // instance, no cross-process signalling, nothing to leak. Exit code 0 = a region
                // was captured and copied, 2 = the user cancelled.
                Out("TRIGGER: capturing inline");
                CaptureInline(outPath);
                return;
            }

            Out("nothing to do: pass --trigger (the plugin always does)");
        }

        /// <summary>
        /// One interactive capture without a tray icon or a hotkey: used when something
        /// outside the process (the in-chat scissors button) asks for a screenshot while no
        /// tray instance is running. Ends with the region on the clipboard, like everything else.
        /// </summary>
        private static void CaptureInline(string outPath)
        {
            IntPtr previous = NativeApi.GetForegroundWindow();
            bool captured = false;
            try
            {
                Rectangle bounds = ScreenGrab.VirtualBounds();
                using (Bitmap shot = ScreenGrab.Capture(bounds))
                {
                    using (ShotForm form = new ShotForm(shot, bounds, false, previous))
                    {
                        form.ShowDialog();
                        if (form.HostHideFailed && outPath != null)
                        {
                            // Hand the reason to the host through a sidecar file: stdio is 'ignore'
                            // in the plugin, so this is the only channel that survives.
                                    string reason = "截图已取消：" + form.HostHideReason;
                            try
                            {
                                File.WriteAllText(outPath + ".error", reason, System.Text.Encoding.UTF8);
                            }
                            catch (Exception ex) { Out("error-file FAILED: " + ex.Message); }
                            Out("TRIGGER REFUSED: " + reason);
                            Environment.ExitCode = 3;
                            return;
                        }
                        if (form.Result != null)
                        {
                            captured = true;
                            try
                            {
                                bool copied = ShotApp.CopyImage(form.Result);
                                Out("clipboard      : " + (copied ? "ok" : "FAILED"));
                                if (outPath != null)
                                {
                                    // The in-chat button needs a path it can read back; saving it
                                    // here is what turns the capture into an attachment.
                                    bool saved = false;
                                    string saveError = null;
                                    try
                                    {
                                        string full = Path.GetFullPath(outPath);
                                        string parent = Path.GetDirectoryName(full);
                                        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent)) Directory.CreateDirectory(parent);
                                        saved = ShotApp.SavePng(form.Result, full);
                                        if (!saved) saveError = "鏃犳硶鍐欏叆 " + full;
                                        Out("out            : " + full + (saved ? "" : " (FAILED)"));
                                    }
                                    catch (Exception ex)
                                    {
                                        saveError = ex.Message;
                                        Out("out FAILED     : " + ex.Message);
                                    }
                                    if (!saved)
                                    {
                                        // Keep the picture on the clipboard, but tell the caller the
                                        // attachment never reached disk instead of exiting as success.
                                        string reason = "截图已保存到剪贴板，但写入文件失败：" + (saveError ?? "unknown");
                                        try { File.WriteAllText(outPath + ".error", reason, System.Text.Encoding.UTF8); }
                                        catch (Exception) { }
                                        Out("TRIGGER REFUSED: " + reason);
                                        Environment.ExitCode = 3;
                                        return;
                                    }
                                }
                                if (outPath == null)
                                {
                                    // Only a caller that gave no explicit destination gets a history
                                    // copy. The in-chat button passes --out into the DSH data
                                    // directory and deletes it after reading, so nothing is left
                                    // behind on this machine.
                                    try
                                    {
                                        string dir = ShotApp.ShotDir();
                                        ShotApp.SavePng(form.Result, Path.Combine(dir, "shot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png"));
                                    }
                                    catch (Exception ex)
                                    {
                                        Out("save skipped   : " + ex.Message);
                                    }
                                }
                            }
                            finally
                            {
                                form.Result.Dispose();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Out("TRIGGER FAILED: " + ex.ToString());
            }
            finally
            {
                string restoreNote;
                Desktop.Restore(IntPtr.Zero, previous, out restoreNote);
                if (restoreNote.Length > 0) Out("restore        : " + restoreNote);
                // 0 = a region reached the clipboard, 2 = cancelled. The caller waits on this
                // process, so the exit code is how it learns whether anything was captured.
                // A refusal already set 3 with a written .error file; do not turn it into a plain cancel.
                if (Environment.ExitCode == 0) Environment.ExitCode = captured ? 0 : 2;
                Out("TRIGGER RESULT " + (captured ? "CAPTURED" : "CANCELLED"));
            }
        }

        private static void Out(string line)
        {
            try
            {
                Console.WriteLine(line);
            }
            catch (Exception)
            {
            }
            ShotApp.Log("[cli] " + line);
        }
    }
}
