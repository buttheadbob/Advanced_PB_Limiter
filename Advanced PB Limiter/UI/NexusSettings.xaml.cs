using System;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Settings;

namespace Advanced_PB_Limiter.UI
{
    public partial class NexusSettings
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        private static Timer NexusConnectionChecker { get; set; } = new Timer(1000);

        public NexusSettings()
        {
            InitializeComponent();
            NexusConnectionChecker.Elapsed += Nexus_Connected;
            NexusConnectionChecker.Interval = 1000;
            NexusConnectionChecker.Start();
        }
        
        private async Task Save()
        {
            await Advanced_PB_Limiter.Instance!.Save();
        }
        
        private void Nexus_Connected(object? sender, EventArgs e)
        {
            if (!Advanced_PB_Limiter.NexusInstalled) return;
            
            ServerNameText.Dispatcher.Invoke(() =>
            {
                ServerNameText.Text = NexusNetworkManager.ThisServer?.Name;
                ServerIPText.Text = NexusNetworkManager.ThisServer?.ServerIP;
                ServerIDText.Text = NexusNetworkManager.ThisServer?.ServerID.ToString();
                ServerTypeText.Text = NexusNetworkManager.ThisServer?.ServerType.ToString();
            });
            
            NexusConnectionChecker.Stop();
        }

        private async void SaveButton_OnClick(object sender, RoutedEventArgs e)
        {
            await Save();
        }
    }
}