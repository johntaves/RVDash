using System;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
        BitmapFrame? frame = null;
        Action closeIt;
        object lockObj = new object();
        bool go = true;
        public Cameras(HttpClient client, string url,Action cl )
        {
            InitializeComponent();
            closeIt = cl;
            this.Closed += Cameras_Closed;
            GetImages(client,url);
        }

        private void Cameras_Closed(object sender, EventArgs e)
        {
            go = false;
            closeIt();
        }
        public void DoImage()
        {
            theImg.Source = frame;
            lock (lockObj) frame = null;
        }
        async Task GetImages(HttpClient client,string url)
        {
            var imageBuffer = new byte[1024 * 1024];
            using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode)
                throw new Exception(resp.StatusCode.ToString());

            var ct = resp.Content.Headers.ContentType;
            if (ct.MediaType != "multipart/x-mixed-replace")
                throw new Exception("Bad media");

            var bb = ct.Parameters.First();

            using var st = await resp.Content.ReadAsStreamAsync();
            var mr = new MultipartReader(bb.Value, st);
            while (go && await mr.ReadNextSectionAsync() is { } section)
            {
                await using var ms = new MemoryStream();
                await section.Body.CopyToAsync(ms);
                bool lockTaken = false;

                try
                {
                    Monitor.TryEnter(lockObj, 1, ref lockTaken);
                    if (lockTaken && frame is null)
                    { // throws extra frames on the floor
                        ms.Position = 0;
                        var image = new JpegBitmapDecoder(ms, BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
                        frame = image.Frames[0];
                        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, new Action(DoImage));
                    }
                }
                finally
                {
                    // Ensure that the lock is released.
                    if (lockTaken) Monitor.Exit(lockObj);
                }
            }
            throw new Exception("");
        }
    }
}
