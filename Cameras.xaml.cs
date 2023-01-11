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
		LibVLC libVLC;
		MediaPlayer mediaPlayer;
		public Cameras()
		{
			InitializeComponent();
			videoView.Loaded += VideoView_Loaded;
		}
		public Cameras(Action cl)
		{
			InitializeComponent();
			videoView.Loaded += VideoView_Loaded;
			this.Closed += (s, e) => { cl(); };
		}
		void VideoView_Loaded(object sender, RoutedEventArgs e)
		{
			Core.Initialize();

			libVLC = new LibVLC(new string[] { "--video-filter=transform", "--transform-type=hflip", "--ipv4-timeout=500" });
			mediaPlayer = new MediaPlayer(libVLC);
			mediaPlayer.EncounteredError += MediaPlayer_EncounteredError;
			videoView.MediaPlayer = mediaPlayer;

			mediaPlayer.Play(new Media(libVLC, new Uri("rtsp://192.168.1.194:554/11")));
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
						break;
					}
				}
			}
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
