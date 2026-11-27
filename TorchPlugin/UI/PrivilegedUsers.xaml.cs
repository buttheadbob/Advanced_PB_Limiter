using System;
using System.Globalization;
using System.Windows;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using MessageBox = System.Windows.MessageBox;

namespace Advanced_PB_Limiter.UI
{
    public partial class PrivilegedUsers
    {
        // The config this tab was built for: a tab Torch keeps after a reload still holds it, so it never reads a plugin
        // instance that is gone.
        private readonly Advanced_PB_LimiterConfig _config = AdvancedPBLimiterPlugin.Instance!.Config!;
        private Advanced_PB_LimiterConfig Config => _config;

        public PrivilegedUsers()
        {
            InitializeComponent();
            DataContext = Config;
        }

        // A whole-number box takes what Limits takes for any limit, so the tab refuses what the commands and Quasar refuse.
        private static bool TryParseCount(string text, out int value)
        {
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && Limits.IsCount(value);
        }

        private static void Refuse(string message)
        {
            MessageBox.Show(message, "Invalid Entry", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void LoadPrivilegedPlayerDataButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (PrivilegedPlayersGrid.SelectedIndex < 0) return;
            
            PrivilegedPlayer? selectedPlayer = (PrivilegedPlayer) PrivilegedPlayersGrid.SelectedItem;
            if (selectedPlayer is null) return;
            
            PlayerNameBox.Text = selectedPlayer.Name;
            SteamIDBox.Text = selectedPlayer.SteamId.ToString(CultureInfo.InvariantCulture);
            GracefulShutdownBox.Text = selectedPlayer.GracefulShutDownRequestDelay.ToString(CultureInfo.InvariantCulture);
            PunishmentBox.Text = selectedPlayer.Punishment.ToString();
            DamageAmountBox.Text = selectedPlayer.DamageAmount.ToString(CultureInfo.InvariantCulture);
            OffencesBeforePunishmentBox.Text = selectedPlayer.OffencesBeforePunishment.ToString(CultureInfo.InvariantCulture);
            MaxRuntimeAllowedBox.Text = selectedPlayer.RuntimeAllowance.ToString(CultureInfo.InvariantCulture);
            MaxAVGRuntimeAllowedBox.Text = selectedPlayer.RuntimeAverageAllowance.ToString(CultureInfo.InvariantCulture);
            NoCombinedLimitsBox.IsChecked = selectedPlayer.NoCombinedLimits;
            MaxCombinedRuntimeAllowedBox.Text = selectedPlayer.CombinedRuntimeAllowance.ToString(CultureInfo.InvariantCulture);
            MaxCombinedAVGRuntimeAllowedBox.Text = selectedPlayer.CombinedRuntimeAverageAllowance.ToString(CultureInfo.InvariantCulture);
            StartupBox.Text = selectedPlayer.StartupAllowance.ToString(CultureInfo.InvariantCulture);
        }

        private void AddOrUpdateButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(PlayerNameBox.Text))
            {
                MessageBox.Show("Player Name cannot be empty", "Invalid Entry", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            if (string.IsNullOrWhiteSpace(SteamIDBox.Text))
            {
                MessageBox.Show("Steam ID cannot be empty", "Invalid Entry", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!ulong.TryParse(SteamIDBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong steamId))
            {
                MessageBox.Show("Steam ID must be a number", "Invalid Entry", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Identities with no Steam ID, NPCs among them, all share 0, so a row for it would apply to every one of them.
            if (steamId == 0)
            {
                Refuse("Steam ID cannot be 0");
                return;
            }

            if (!TryParseCount(GracefulShutdownBox.Text, out int gracefulShutDown))
            {
                Refuse("Graceful Shutdown must be a whole number, 0 or more");
                return;
            }
            
            if (!TryParseCount(OffencesBeforePunishmentBox.Text, out int offencesBeforePunishment))
            {
                Refuse("Offences Before Punishment must be a whole number, 0 or more");
                return;
            }
            
            // 0 means the server's damage amount; a negative one would be saved and then quietly replaced by it.
            if (!TryParseCount(DamageAmountBox.Text, out int damageAmount))
            {
                Refuse("Damage Amount must be a whole number, 0 or more (0 uses the server's damage amount)");
                return;
            }
            
            if (!Limits.TryParse(MaxRuntimeAllowedBox.Text, out double maxRuntimeAllowed))
            {
                Refuse("Max Runtime Allowed must be a number, 0 or more, with a dot for decimals");
                return;
            }
            
            if (!Limits.TryParse(MaxAVGRuntimeAllowedBox.Text, out double maxAvgRuntimeAllowed))
            {
                Refuse("Max Average Runtime Allowed must be a number, 0 or more, with a dot for decimals");
                return;
            }
            
            if (!Limits.TryParse(MaxCombinedRuntimeAllowedBox.Text, out double maxCombinedRuntimeAllowed))
            {
                Refuse("Max Combined Runtime Allowed must be a number, 0 or more, with a dot for decimals");
                return;
            }
            
            if (!Limits.TryParse(MaxCombinedAVGRuntimeAllowedBox.Text, out double maxCombinedAvgRuntimeAllowed))
            {
                Refuse("Max Combined Average Runtime Allowed must be a number, 0 or more, with a dot for decimals");
                return;
            }
            
            if (!Limits.TryParse(StartupBox.Text, out double startup))
            {
                Refuse("Startup must be a number, 0 or more, with a dot for decimals");
                return;
            }
            
            if (!Enum.TryParse(PunishmentBox.Text, out Enums.Punishment punishment))
            {
                MessageBox.Show("Punishment must be a valid punishment", "Invalid Entry", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            PrivilegedPlayer newPlayer = new()
            {
                Name = PlayerNameBox.Text,
                SteamId = steamId,
                GracefulShutDownRequestDelay = gracefulShutDown,
                OffencesBeforePunishment = offencesBeforePunishment,
                DamageAmount = damageAmount,
                RuntimeAllowance = maxRuntimeAllowed,
                RuntimeAverageAllowance = maxAvgRuntimeAllowed,
                CombinedRuntimeAllowance = maxCombinedRuntimeAllowed,
                CombinedRuntimeAverageAllowance = maxCombinedAvgRuntimeAllowed,
                StartupAllowance = startup,
                Punishment = punishment,
                NoCombinedLimits = NoCombinedLimitsBox.IsChecked ?? false
            };

            Config.SetPrivilegedPlayer(newPlayer);
            _ = Advanced_PB_LimiterControl.SaveConfigAsync();
            PrivilegedPlayersGrid.ItemsSource = null;
            PrivilegedPlayersGrid.ItemsSource = Config.PrivilegedPlayers.Values;
        }

        private void RemoveButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (PrivilegedPlayersGrid.SelectedIndex < 0) return;
            
            PrivilegedPlayer? selectedPlayer = (PrivilegedPlayer) PrivilegedPlayersGrid.SelectedItem;
            if (selectedPlayer is null) return;
            PrivilegedPlayersGrid.ItemsSource = null;
            Config.PrivilegedPlayers.Remove(selectedPlayer.SteamId);
            _ = Advanced_PB_LimiterControl.SaveConfigAsync();
            PrivilegedPlayersGrid.ItemsSource = Config.PrivilegedPlayers.Values;
        }

    }
}