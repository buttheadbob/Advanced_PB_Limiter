using System;
using System.Threading.Tasks;
using System.Windows;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Settings;

namespace Advanced_PB_Limiter.UI
{
    public partial class NexusSettings
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;

        public NexusSettings()
        {
            InitializeComponent();
            NexusManager.NexusConnected += Nexus_Connected;
        }
        
        private async Task Save()
        {
            await Advanced_PB_Limiter.Instance!.Save();
        }
        
        private void Nexus_Connected(object? sender, EventArgs e)
        {
            if (e is not NexusConnectedEventArgs args) return;
            if (args.Connected)
            {
                Advanced_PB_Limiter.Dispatcher.Invoke(() =>
                {
                    ServerNameText.Text = NexusManager.ThisServer?.Name;
                    ServerIPText.Text = NexusManager.ThisServer?.ServerIP;
                    ServerIDText.Text = NexusManager.ThisServer?.ServerID.ToString();
                    ServerTypeText.Text = NexusManager.ThisServer?.ServerType.ToString();
                });
                return;
            }
            
            Advanced_PB_Limiter.Dispatcher.Invoke(() =>
            {
                ServerNameText.Text = "Not Connected";
                ServerIPText.Text = "Not Connected";
                ServerIDText.Text = "Not Connected";
                ServerTypeText.Text = "Not Connected";
            });
        }

        private async void SaveButton_OnClick(object sender, RoutedEventArgs e)
        {
            await Save();
        }
    }
}