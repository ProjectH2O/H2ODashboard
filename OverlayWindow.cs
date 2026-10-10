using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace H2ODashboard
{
    /// <summary>
    /// Borderless, always-on-top window that shows a skin with real per-pixel
    /// transparency over the game (Borderless/Windowed display modes).
    ///
    /// WebView2 renders through DirectComposition into a window without a
    /// redirection bitmap (WS_EX_NOREDIRECTIONBITMAP), so transparent parts of the
    /// page show what's behind. Locked: click-through (WS_EX_LAYERED |
    /// WS_EX_TRANSPARENT). Edit mode: drag to move, edges to resize.
    /// Never takes focus away from the game (WS_EX_NOACTIVATE).
    /// </summary>
    public sealed class OverlayWindow : Form
    {
        const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000;
        const int WS_EX_NOREDIRECTIONBITMAP = 0x200000, WS_EX_NOACTIVATE = 0x8000000;
        const int GWL_EXSTYLE = -20, LWA_ALPHA = 2;
        const int WM_NCHITTEST = 0x84, WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
        const int HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        readonly Func<string> _url;
        readonly string _userDataFolder;
        CoreWebView2CompositionController _controller;
        object _dcompDevice, _dcompTarget, _rootVisual, _webVisual;
        bool _editing;

        public event Action BoundsSaved;

        // WebView2's composition mode also creates its own top-level input window
        // (Chrome_WidgetWin_1 in the browser process) exactly over ours, which would
        // catch every click. Keep that window click-through too; re-applied periodically
        // in case WebView2 recreates or restyles it.
        readonly Timer _clickThroughTimer = new Timer { Interval = 1000 };

        public OverlayWindow(Func<string> url, string userDataFolder, Rectangle bounds)
        {
            _url = url;
            _userDataFolder = userDataFolder;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            MinimumSize = new Size(80, 40);
            Text = "H2Overdrive Dashboard Overlay";
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE |
                              WS_EX_LAYERED | WS_EX_TRANSPARENT;
                return cp;
            }
        }

        public bool Editing
        {
            get => _editing;
            set
            {
                _editing = value;
                int ex = GetWindowLong(Handle, GWL_EXSTYLE);
                ex = value ? ex & ~(WS_EX_LAYERED | WS_EX_TRANSPARENT) : ex | WS_EX_LAYERED | WS_EX_TRANSPARENT;
                SetWindowLong(Handle, GWL_EXSTYLE, ex);
                if (!value)
                    SetLayeredWindowAttributes(Handle, 0, 255, LWA_ALPHA);
                _controller?.CoreWebView2?.ExecuteScriptAsync(
                    $"document.documentElement.classList.toggle('h2o-edit', {(value ? "true" : "false")})");
                if (!value)
                    BoundsSaved?.Invoke();
            }
        }

        protected override async void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetLayeredWindowAttributes(Handle, 0, 255, LWA_ALPHA);
            try
            {
                await InitializeWebView();
            }
            catch (Exception ex)
            {
                MessageBox.Show("The overlay needs the Microsoft Edge WebView2 Runtime.\n\n" + ex.Message,
                    "H2Overdrive Dashboard", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        async Task InitializeWebView()
        {
            // DirectComposition: hwnd target -> root visual -> WebView2's visual
            var iid = typeof(IDCompositionDevice).GUID;
            Marshal.ThrowExceptionForHR(DCompositionCreateDevice2(IntPtr.Zero, ref iid, out _dcompDevice));
            var device = (IDCompositionDevice)_dcompDevice;
            Marshal.ThrowExceptionForHR(device.CreateTargetForHwnd(Handle, true, out var target));
            _dcompTarget = target;
            Marshal.ThrowExceptionForHR(device.CreateVisual(out var root));
            Marshal.ThrowExceptionForHR(device.CreateVisual(out var web));
            _rootVisual = root;
            _webVisual = web;
            Marshal.ThrowExceptionForHR(((IDCompositionTarget)target).SetRoot(root));
            Marshal.ThrowExceptionForHR(((IDCompositionVisual)root).AddVisual(web, true, null));

            var environment = await CoreWebView2Environment.CreateAsync(null, _userDataFolder);
            _controller = await environment.CreateCoreWebView2CompositionControllerAsync(Handle);
            _controller.DefaultBackgroundColor = Color.Transparent;
            _controller.RootVisualTarget = web;
            _controller.Bounds = new Rectangle(Point.Empty, ClientSize);
            _controller.IsVisible = true;
            _controller.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _controller.CoreWebView2.Settings.IsZoomControlEnabled = false;
            _controller.CoreWebView2.NavigationCompleted += (s, e) =>
            {
                if (_editing)
                    _controller.CoreWebView2.ExecuteScriptAsync("document.documentElement.classList.add('h2o-edit')");
            };
            Marshal.ThrowExceptionForHR(device.Commit());
            Navigate();

            _clickThroughTimer.Tick += (s, e) => MakeWebViewWindowsClickThrough();
            _clickThroughTimer.Start();
            MakeWebViewWindowsClickThrough();
        }

        void MakeWebViewWindowsClickThrough()
        {
            if (_controller?.CoreWebView2 == null)
                return;
            uint browserPid = _controller.CoreWebView2.BrowserProcessId;
            GetWindowRect(Handle, out var ours);
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid != browserPid || !GetWindowRect(hwnd, out var r))
                    return true;
                // Only the window WebView2 keeps exactly over our overlay (not e.g. a SkinWindow
                // that shares the same browser process and happens to overlap)
                if (r.Left == ours.Left && r.Top == ours.Top && r.Right == ours.Right && r.Bottom == ours.Bottom)
                {
                    int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                    if ((ex & (WS_EX_TRANSPARENT | WS_EX_LAYERED)) != (WS_EX_TRANSPARENT | WS_EX_LAYERED))
                        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_LAYERED);
                }
                return true;
            }, IntPtr.Zero);
        }

        public void Navigate() => _controller?.CoreWebView2?.Navigate(_url());

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_controller != null)
                _controller.Bounds = new Rectangle(Point.Empty, ClientSize);
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            // Lets WebView2 move its input window along with ours
            _controller?.NotifyParentWindowPositionChanged();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Nothing: there is no redirection surface, WebView2 draws everything
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                m.Result = (IntPtr)MA_NOACTIVATE;
                return;
            }
            if (m.Msg == WM_NCHITTEST && _editing)
            {
                var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
                const int edge = 10;
                bool left = p.X < edge, right = p.X >= ClientSize.Width - edge, top = p.Y < edge, bottom = p.Y >= ClientSize.Height - edge;
                m.Result = (IntPtr)(top && left ? HTTOPLEFT : top && right ? HTTOPRIGHT : bottom && left ? HTBOTTOMLEFT :
                    bottom && right ? HTBOTTOMRIGHT : left ? HTLEFT : right ? HTRIGHT : top ? HTTOP : bottom ? HTBOTTOM : HTCAPTION);
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _clickThroughTimer.Stop();
            _controller?.Close();
            _controller = null;
            foreach (var com in new[] { _webVisual, _rootVisual, _dcompTarget, _dcompDevice })
                if (com != null)
                    Marshal.ReleaseComObject(com);
            base.OnFormClosed(e);
        }

        // ---- interop ---------------------------------------------------------

        [DllImport("dcomp.dll")]
        static extern int DCompositionCreateDevice2(IntPtr renderingDevice, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object device);

        [DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        [DllImport("user32.dll")]
        static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, int flags);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        // Only the methods used here are declared with real signatures; the rest are
        // placeholders that keep the vtable slots in order (dcomp.h).
        [ComImport, Guid("C37EA93A-E7AA-450D-B16F-9746CB0407F3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDCompositionDevice
        {
            [PreserveSig] int Commit();
            [PreserveSig] int WaitForCommitCompletion();
            [PreserveSig] int GetFrameStatistics(IntPtr statistics);
            [PreserveSig] int CreateTargetForHwnd(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool topmost,
                [MarshalAs(UnmanagedType.IUnknown)] out object target);
            [PreserveSig] int CreateVisual([MarshalAs(UnmanagedType.IUnknown)] out object visual);
        }

        [ComImport, Guid("EACDD04C-117E-4E17-88F4-D1B12B0E3D89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDCompositionTarget
        {
            [PreserveSig] int SetRoot([MarshalAs(UnmanagedType.IUnknown)] object visual);
        }

        [ComImport, Guid("4D93059D-097B-4651-9A60-F0F25116E2F3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDCompositionVisual
        {
            void _SetOffsetX1(); void _SetOffsetX2(); void _SetOffsetY1(); void _SetOffsetY2();
            void _SetTransform1(); void _SetTransform2(); void _SetTransformParent(); void _SetEffect();
            void _SetBitmapInterpolationMode(); void _SetBorderMode(); void _SetClip1(); void _SetClip2(); void _SetContent();
            [PreserveSig] int AddVisual([MarshalAs(UnmanagedType.IUnknown)] object visual, [MarshalAs(UnmanagedType.Bool)] bool insertAbove,
                [MarshalAs(UnmanagedType.IUnknown)] object referenceVisual);
        }
    }
}
