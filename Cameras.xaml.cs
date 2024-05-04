using System;
using System.CodeDom;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Policy;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.AspNetCore.WebUtilities;

namespace RVDash
{

	/// <summary>
	/// Interaction logic for Cameras.xaml
	/// </summary>
	public partial class Cameras : Window
    {
		private const int GWL_STYLE = -16;
		private const int WS_SYSMENU = 0x80000;
		[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
		private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
		[System.Runtime.InteropServices.DllImport("user32.dll")]
		private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

		volatile BitmapFrame? frame = null;
        HttpClient client = new();
        CancellationTokenSource? ctSrc = null;
        string url;
        public Cameras(string url)
        {
            InitializeComponent();
			this.Closed += Cameras_Closed;
			this.IsVisibleChanged += Cameras_IsVisibleChanged;
			this.Loaded += Cameras_Loaded;
			//client.Timeout = TimeSpan.FromSeconds(5);
			this.url = url;
        }
		private void Cameras_Loaded(object sender, RoutedEventArgs e)
		{
			var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
			SetWindowLong(hwnd, GWL_STYLE, GetWindowLong(hwnd, GWL_STYLE) & ~WS_SYSMENU);
		}
		private void Cameras_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
			if (ctSrc != null && !ctSrc.IsCancellationRequested)
				ctSrc.Cancel();
            ctSrc = null;
			if (!IsVisible)
                return;
            if (Screen.AllScreens.Length > 1)
            {
                foreach (Screen s in Screen.AllScreens)
                {
                    if (s.Primary)
                    {
                        var scaleRatio = Math.Max(Screen.PrimaryScreen.WorkingArea.Width / SystemParameters.PrimaryScreenWidth,
                                        Screen.PrimaryScreen.WorkingArea.Height / SystemParameters.PrimaryScreenHeight);
                        this.Left = s.WorkingArea.Left / scaleRatio;
                        this.Top = s.WorkingArea.Top / scaleRatio;
                        this.WindowStyle = WindowStyle.None;
                        this.Width = Screen.PrimaryScreen.WorkingArea.Width / scaleRatio;
                        this.Height = 768 / scaleRatio;
                        //this.WindowState = WindowState.Maximized;
                        break;
                    }
                }
            }
            else
            {
                this.WindowStyle = WindowStyle.ThreeDBorderWindow;
                this.WindowState = WindowState.Normal;
            }
            ctSrc = new();
			GetImages(client, url, ctSrc.Token);
        }
		private void Cameras_Closed(object sender, EventArgs e) => client.Dispose();
        public void DoImage()
        {
            if (frame is null)
                return;
            theImg.Source = frame;
            frame = null;
        }
        async Task GetImages(HttpClient client, string url, CancellationToken canTok)
        {
			//Trace.WriteLine("Looping");
			var imageBuffer = new byte[1024 * 1024];
            while (!ctSrc.IsCancellationRequested)
            {
                try
                {
					using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, canTok);
                    if (!resp.IsSuccessStatusCode)
                        throw new Exception(resp.StatusCode.ToString());

                    var ct = resp.Content.Headers.ContentType;
                    if (ct.MediaType != "multipart/x-mixed-replace")
                        throw new Exception("Bad media");

                    var bb = ct.Parameters.First();

                    using var st = await resp.Content.ReadAsStreamAsync(canTok);
                    var mr = new MultipartReader(bb.Value, st);
                    while (!canTok.IsCancellationRequested)
                    {
                        if (!(await mr.ReadNextSectionAsync(canTok) is { } section))
                            continue;
                        if (frame != null) // ignores the frame if UI is not ready for another frame
                            continue;
                        using var ms = new MemoryStream();
                        await section.Body.CopyToAsync(ms, canTok);
                        ms.Position = 0;
                        var image = new JpegBitmapDecoder(ms, BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
                        frame = image.Frames[0];
                        await Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(DoImage));
                    }
                }
                catch (TaskCanceledException) { throw; }
                catch(Exception e)
                {
                }
                finally { }
            }
        }
    }
}
