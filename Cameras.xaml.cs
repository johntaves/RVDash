﻿using System;
using System.CodeDom;
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
        volatile BitmapFrame? frame = null;
        Action closeIt;
        volatile bool go = true;
        public Cameras(HttpClient client, string url,Action cl )
        {
            InitializeComponent();
            closeIt = cl;
            this.Closed += Cameras_Closed;
			this.Loaded += Cameras_Loaded;
            GetImages(client,url);
        }

		private void Cameras_Loaded(object sender, RoutedEventArgs e)
		{
            CheckScreen();
		}

		void CheckScreen()
		{
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
		}

		private void Cameras_Closed(object sender, EventArgs e)
        {
            go = false;
            closeIt();
        }
        public void DoImage()
        {
            if (frame is null)
                return;
            theImg.Source = frame;
            frame = null;
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
            while (go)
            {
                try
                {
                    if (!(await mr.ReadNextSectionAsync() is { } section))
                        continue;
                    if (frame != null) // ignores the frame if UI is not ready for another frame
                        continue;
                    await using var ms = new MemoryStream();
                    await section.Body.CopyToAsync(ms);
                    ms.Position = 0;
                    var image = new JpegBitmapDecoder(ms, BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
                    frame = image.Frames[0];
                    await Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, new Action(DoImage));
                }
                catch (WebException) { }
            }
        }
    }
}
