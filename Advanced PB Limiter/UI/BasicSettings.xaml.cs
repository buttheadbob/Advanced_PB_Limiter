using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;

namespace Advanced_PB_Limiter.UI
{
    public partial class BasicSettings
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        private static Timer _burpTimer = new (5000);

        public BasicSettings()
        {
            InitializeComponent();
            DataContext = Config;
            PunishmentComboBox.Text = Config.Punishment.ToString();
            ComboBoxItem item1 = new() { Content = "TurnOff" };
            ComboBoxItem item2 = new() { Content = "Damage" };
            ComboBoxItem item3 = new() { Content = "Destroy" };
            
            PunishmentComboBox.Items.Add(item1);
            PunishmentComboBox.Items.Add(item2);
            PunishmentComboBox.Items.Add(item3);

            PunishmentLabel.Text = $"Punishment type: [Active:{Config.Punishment.ToString()}]";

            _burpTimer.Elapsed += (sender, args) => EndBurp();
            Config.ShowTimingsBurp = false;
        }

        private void EndBurp()
        {
            _burpTimer.Stop();
            Config.ShowTimingsBurp = false;
        }

        private void Save()
        {
            Advanced_PB_Limiter.Instance!.Save();
        }

        private void SaveLocallyButton_OnClick(object sender, RoutedEventArgs e)
        {
            Save();
        }

        private void SaveAndPush_OnClick(object sender, RoutedEventArgs e)
        {
            Save();
            NexusNetworkManager.UpdateNexusWithSettingsData();
        }

        private void PunishmentComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ComboBoxItem item = (ComboBoxItem) PunishmentComboBox.SelectedItem;
            Config.Punishment = item.Content.ToString() switch
            {
                "Turn Off" => Config.Punishment = Enums.Punishment.TurnOff,
                "Damage" => Config.Punishment = Enums.Punishment.Damage,
                "Destroy" => Config.Punishment = Enums.Punishment.Destroy,
                _ => Config.Punishment = Enums.Punishment.TurnOff
            };
            PunishmentLabel.Text = $"Punishment type: [Active:{Config.Punishment.ToString()}]";
        }

        private void TimingBurp_OnClick(object sender, RoutedEventArgs e)
        {
            Config.ShowTimingsBurp = true;
            _burpTimer.Start();
        }
    }
}