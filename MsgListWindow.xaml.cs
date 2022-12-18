using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

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
		}
		void Window_Loaded(object sender, RoutedEventArgs e)
		{
			//Set the current value of the gauges
			this.lstCodes.ItemsSource = MsgList;
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
