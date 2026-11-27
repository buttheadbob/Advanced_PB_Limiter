using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Advanced_PB_Limiter.Commands;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;

namespace Advanced_PB_Limiter.UI
{
    public partial class BasicSettings
    {
        // The config this tab was built for: a tab Torch keeps after a reload still holds it, so it never reads a plugin
        // instance that is gone.
        private readonly Advanced_PB_LimiterConfig _config = AdvancedPBLimiterPlugin.Instance!.Config!;
        private Advanced_PB_LimiterConfig Config => _config;

        public BasicSettings()
        {
            InitializeComponent();
            DataContext = Config;
            FillPunishments();
            // The box is not bound: it follows the setting, which changes when the file is read after the tab was built.
            Config.PropertyChanged += OnConfigChanged;
        }

        private void FillPunishments()
        {
            // The items are in the order of the enum, so the selected index is the punishment.
            foreach (string punishment in Enum.GetNames(typeof(Enums.Punishment)))
                PunishmentComboBox.Items.Add(new ComboBoxItem { Content = punishment });
            ShowPunishment();
        }

        // Raised on whichever thread changed the setting, the config read's pool thread among them.
        private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(Advanced_PB_LimiterConfig.Punishment)) return;
            _ = Dispatcher.BeginInvoke(new Action(ShowPunishment));
        }

        // Selecting the item the setting already holds sets nothing, so this arms no save.
        private void ShowPunishment()
        {
            PunishmentComboBox.SelectedIndex = (int)Config.Punishment;
            PunishmentLabel.Text = $"Punishment type: [Active:{Config.Punishment.ToString()}]";
        }

        private void SaveLocallyButton_OnClick(object sender, RoutedEventArgs e)
        {
            _ = Advanced_PB_LimiterControl.SaveConfigAsync();
        }

        private void PunishmentComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PunishmentComboBox.SelectedIndex < 0) return;

            Config.Punishment = (Enums.Punishment)PunishmentComboBox.SelectedIndex;
            PunishmentLabel.Text = $"Punishment type: [Active:{Config.Punishment.ToString()}]";
        }

        private void TimingBurp_OnClick(object sender, RoutedEventArgs e)
        {
            string reply = LimiterCommands.ShowTimings();
            if (LimiterCore.Retired)
                System.Windows.MessageBox.Show(reply, "Advanced PB Limiter", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
