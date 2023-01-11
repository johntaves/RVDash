using LibVLCSharp.Shared;
using System;
using System.Windows;


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

			_libVLC = new LibVLC();
			_mediaPlayer = new MediaPlayer(_libVLC);

			videoView.MediaPlayer = _mediaPlayer;

			_mediaPlayer.Play(new Media(_libVLC, new Uri("rtsp://192.168.1.194:554/11")));
		}

	}
}
