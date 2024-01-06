using System.Threading.Tasks;
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

        public BasicSettings()
        {
            InitializeComponent();
            DataContext = Config;
            PunishmentComboBox.Text = Config.Punishment.ToString();
            ComboBoxItem item1 = new() { Content = "Turn Off" };
            ComboBoxItem item2 = new() { Content = "Damage" };
            ComboBoxItem item3 = new() { Content = "Destroy" };
            
            PunishmentComboBox.Items.Add(item1);
            PunishmentComboBox.Items.Add(item2);
            PunishmentComboBox.Items.Add(item3);

            PunishmentLabel.Text = $"Punishment type: [Active:{Config.Punishment.ToString()}]";
        }

        private async Task Save()
        {
            await Advanced_PB_Limiter.Instance!.Save();
        }

        private async void SaveLocallyButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(RemovePlayersWithNoPbFrequency.Text, out int newFrequency))
            {
                TrackingManager.StartPlayerCleanupTimer(newFrequency);
            }
            else
            {
                MessageBox.Show("Invalid Player Cleanup Frequency");
                RemovePlayersWithNoPbFrequency.Focus();
            }
            await Save();
        }

        private async void SaveAndPush_OnClick(object sender, RoutedEventArgs e)
        {
            await Save();
            await NexusManager.UpdateNexusWithSettingsData();
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
    }
}