using LibVLCSharp.Shared;
using System;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using static System.Net.WebRequestMethods;

namespace RVDash
{
	/// <summary>
	/// Interaction logic for Cameras.xaml
	/// </summary>
	public partial class Cameras : Window
	{
		LibVLC libVLC = null;
		Action closeIt;
		Media media = null;
		MediaPlayer mediaPlayer = null;

		public Cameras()
		{
			throw new NotImplementedException();
		}
		public Cameras(LibVLC li, Action cl)
		{
			libVLC = li;
			InitializeComponent();
			closeIt = cl;
			videoView.Loaded += VideoView_Loaded;
			this.Closed += Cameras_Closed;
		}

		private void Cameras_Closed(object sender, EventArgs e)
		{
			if (media != null)
				media.Dispose();
			media = null;
			if (mediaPlayer != null)
				mediaPlayer.Dispose();
			mediaPlayer = null;
			closeIt();
		}

		void VideoView_Loaded(object sender, RoutedEventArgs e)
		{
			mediaPlayer = new MediaPlayer(libVLC);
			mediaPlayer.EncounteredError += MediaPlayer_EncounteredError;
//			mediaPlayer.Playing += MediaPlayer_Playing;
			videoView.MediaPlayer = mediaPlayer;
			media = new Media(libVLC, new Uri("rtsp://192.168.1.194:554/11"));
			mediaPlayer.Play(media);
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
						this.Width = s.WorkingArea.Width / scaleRatio;
						this.Height = this.Width * 1080 / 1920;
						break;
					}
				}
			}
		}
		void SetSize()
		{
			uint w = 0, h = 0;
			mediaPlayer.Size(0, ref w, ref h);
			if (h > 0 && w > 0)
			{
				this.Height = h;
				this.Width = w;
			}
		}
		private void MediaPlayer_Playing(object sender, EventArgs e)
		{
			Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(SetSize));
		}
		void Restart()
		{
			mediaPlayer.Play(new Media(libVLC, new Uri("rtsp://192.168.1.194:554/11")));
		}
		private void MediaPlayer_EncounteredError(object sender, EventArgs e)
		{
			Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Restart));
		}
	}
}
