using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.IO.IsolatedStorage;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace RVDash
{
	/// <summary>
	/// Interaction logic for App.xaml
	/// </summary>
	public partial class App : Application
	{
        private void App_Exit(object sender, ExitEventArgs e)
        {
            RVDash.Properties.Settings.Default.Save();
        }
    }
}
