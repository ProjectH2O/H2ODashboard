using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace H2ODashboard
{
    /// <summary>Normal resizable window showing a skin (second monitor, OBS window capture).</summary>
    public sealed class SkinWindow : Form
    {
        readonly WebView2 _web = new WebView2 { Dock = DockStyle.Fill };
        readonly Func<string> _url;

        public SkinWindow(Func<string> url, string userDataFolder, Rectangle bounds)
        {
            _url = url;
            Text = "H2Overdrive Dashboard";
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            BackColor = Color.Black;
            _web.CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = userDataFolder };
            _web.DefaultBackgroundColor = Color.Black;
            Controls.Add(_web);
        }

        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try
            {
                await _web.EnsureCoreWebView2Async();
                _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                Navigate();
            }
            catch (Exception ex)
            {
                MessageBox.Show("This window needs the Microsoft Edge WebView2 Runtime.\n\n" + ex.Message,
                    "H2Overdrive Dashboard", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        public void Navigate() => _web.CoreWebView2?.Navigate(_url());
    }
}
