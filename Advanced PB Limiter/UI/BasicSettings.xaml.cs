using System.Threading.Tasks;
using System.Windows;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Settings;

namespace Advanced_PB_Limiter.UI
{
    public partial class BasicSettings
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;

        public BasicSettings()
        {
            InitializeComponent();
            DataContext = Config;
        }

        private async Task Save()
        {
            await Advanced_PB_Limiter.Instance!.Save();
        }

        private async void SaveLocallyButton_OnClick(object sender, RoutedEventArgs e)
        {
            await Save();
        }

        private async void SaveAndPush_OnClick(object sender, RoutedEventArgs e)
        {
            await Save();
            await NexusManager.UpdateNexusWithSettingsData();
        }
    }
}