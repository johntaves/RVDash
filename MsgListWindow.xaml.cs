using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;

namespace RVDash
{
    /// <summary>
    /// Interaction logic for MsgListWindow.xaml
    /// </summary>
    public partial class MsgListWindow : Window
    {
		public ObservableCollection<Msg> MsgList = new();
		public MsgListWindow()
        {
            InitializeComponent();
			this.Loaded += new RoutedEventHandler(Window_Loaded);
		//	this.LocationChanged += Window_LocationChanged;
		}
		void Window_Loaded(object sender, RoutedEventArgs e)
		{
			//Set the current value of the gauges
			this.lstCodes.ItemsSource = MsgList;
		}
		void Window_LocationChanged(object sender, System.EventArgs e)
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
						break;
					}
				}
			}
		}
		private void Row_Click(object sender, RoutedEventArgs e)
		{
			var msg = (Msg)((System.Windows.Controls.Button)sender).DataContext;
			msg.cnts = !msg.cnts;
		}
		public bool ShowErr
		{
			get
			{
				foreach (Msg m in MsgList)
					if (m.cnts)
						return true;
				return false;
			}
		}
		public void AddToList(Msg m)
		{
			var i = MsgList.IndexOf(m);
			if (i >= 0)
			{
				m.cnt = MsgList[i].cnt + 1;
				MsgList.RemoveAt(i);
				MsgList.Insert(i, m);
			}
			else
			{
				for (i = 0; i < MsgList.Count && MsgList[i].LessThan(m); i++) ;
				MsgList.Insert(i, m);
			}
		}
	}
}
