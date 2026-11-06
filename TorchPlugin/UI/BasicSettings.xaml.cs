using System;
using System.Windows;
using System.Windows.Controls;
using Advanced_PB_Limiter.Commands;
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

            // The items are in the order of the enum, so the selected index is the punishment.
            foreach (string punishment in Enum.GetNames(typeof(Enums.Punishment)))
                PunishmentComboBox.Items.Add(new ComboBoxItem { Content = punishment });

            PunishmentComboBox.SelectedIndex = (int)Config.Punishment;
            PunishmentLabel.Text = $"Punishment type: [Active:{Config.Punishment.ToString()}]";
        }

        private void Save()
        {
            Advanced_PB_Limiter.Instance!.Save();
        }

        private void SaveLocallyButton_OnClick(object sender, RoutedEventArgs e)
        {
            Save();
        }

        private void PunishmentComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PunishmentComboBox.SelectedIndex < 0) return;

            Config.Punishment = (Enums.Punishment)PunishmentComboBox.SelectedIndex;
            PunishmentLabel.Text = $"Punishment type: [Active:{Config.Punishment.ToString()}]";
        }

        private void TimingBurp_OnClick(object sender, RoutedEventArgs e)
        {
            LimiterCommands.ShowTimings();
        }
    }
}
