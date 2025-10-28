using System;
using System.Collections.Generic;
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

namespace SpinningWheelLib
{
    /// <summary>
    /// Interaction logic for SettingsWindow.xaml
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private bool IsWindows11OrGreater => Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22000;
        public SettingsWindow()
        {
            try
            {
                if (IsWindows11OrGreater)
                {
                    var fluentUri = new Uri("pack://application:,,,/SpinningWheelLib;component/Dictionary1.xaml", UriKind.Absolute);
                    Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = fluentUri
                    });
                }
                InitializeComponent();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error initializing SettingsWindow: {ex.Message}");
            }
        }
    }
}
