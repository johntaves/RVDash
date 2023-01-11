using LibVLCSharp.Shared;
using System;
using System.Windows;
using System.Windows.Forms;

namespace RVDash
{
	/// <summary>
	/// Interaction logic for Cameras.xaml
	/// </summary>
	public partial class Cameras : Window
	{
		LibVLC _libVLC;
		MediaPlayer _mediaPlayer;
		public Cameras()
		{
			InitializeComponent();
			videoView.Loaded += VideoView_Loaded;
		}

		void VideoView_Loaded(object sender, RoutedEventArgs e)
		{
			Core.Initialize();

			_libVLC = new LibVLC(new string[] { "--video-filter=transform", "--transform-type=hflip" });
			_mediaPlayer = new MediaPlayer(_libVLC);
			videoView.MediaPlayer = _mediaPlayer;
			_mediaPlayer.Play(new Media(_libVLC, new Uri("rtsp://192.168.1.194:554/11")));
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

	}
}
