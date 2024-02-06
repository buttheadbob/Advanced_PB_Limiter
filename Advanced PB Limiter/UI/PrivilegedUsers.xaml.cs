using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using MessageBox = System.Windows.MessageBox;

namespace Advanced_PB_Limiter.UI
{
    public partial class PrivilegedUsers
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        public PrivilegedUsers()
        {
            InitializeComponent();
            DataContext = Config;
        }
        
        private async Task Save()
        {
            await Advanced_PB_Limiter.Instance!.Save();
        }

        private void LoadPrivilegedPlayerDataButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (PrivilegedPlayersGrid.SelectedIndex < 0) return;
            
            PrivilegedPlayer? selectedPlayer = (PrivilegedPlayer) PrivilegedPlayersGrid.SelectedItem;
            if (selectedPlayer is null) return;
            
            PlayerNameBox.Text = selectedPlayer.Name;
            SteamIDBox.Text = selectedPlayer.SteamId.ToString();
            GracefulShutdownBox.Text = selectedPlayer.GracefulShutDownRequestDelay.ToString();
            PunishmentBox.Text = selectedPlayer.Punishment.ToString();
            DamageAmountBox.Text = selectedPlayer.DamageAmount.ToString();
            OffencesBeforePunishmentBox.Text = selectedPlayer.OffencesBeforePunishment.ToString();
            MaxRuntimeAllowedBox.Text = selectedPlayer.RuntimeAllowance.ToString(CultureInfo.InvariantCulture);
            MaxAVGRuntimeAllowedBox.Text = selectedPlayer.RuntimeAverageAllowance.ToString(CultureInfo.InvariantCulture);
            NoCombinedLimitsBox.IsChecked = selectedPlayer.NoCombinedLimits;
            MaxCombinedRuntimeAllowedBox.Text = selectedPlayer.CombinedRuntimeAllowance.ToString(CultureInfo.InvariantCulture);
            MaxCombinedAVGRuntimeAllowedBox.Text = selectedPlayer.CombinedRuntimeAverageAllowance.ToString(CultureInfo.InvariantCulture);
            StartupBox.Text = selectedPlayer.StartupAllowance.ToString(CultureInfo.InvariantCulture);
        }

        private async void AddOrUpdateButton_OnClick(object sender, RoutedEventArgs e)
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

            if (!ulong.TryParse(SteamIDBox.Text, out ulong steamId))
            {
                MessageBox.Show("Steam ID must be a number", "Invalid Entry", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!int.TryParse(GracefulShutdownBox.Text, out int gracefulShutDown))
            {
                MessageBox.Show("Graceful Shutdown must be a number", "Invalid Entry", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            if (!int.TryParse(OffencesBeforePunishmentBox.Text, out int offencesBeforePunishment))
            {
                MessageBox.Show("Offences Before Punishment must be a number", "Invalid Entry", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            if (!int.TryParse(DamageAmountBox.Text, out int damageAmount))
            {
                MessageBox.Show("Damage Amount must be a number", "Invalid Entry", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            if (!double.TryParse(MaxRuntimeAllowedBox.Text, out double maxRuntimeAllowed))
            {
                MessageBox.Show("Max Runtime Allowed must be a number", "Invalid Entry", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            if (!double.TryParse(MaxAVGRuntimeAllowedBox.Text, out double maxAvgRuntimeAllowed))
            {
                MessageBox.Show("Max Average Runtime Allowed must be a number", "Invalid Entry", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            if (!double.TryParse(MaxCombinedRuntimeAllowedBox.Text, out double maxCombinedRuntimeAllowed))
            {
                MessageBox.Show("Max Combined Runtime Allowed must be a number", "Invalid Entry", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            if (!double.TryParse(MaxCombinedAVGRuntimeAllowedBox.Text, out double maxCombinedAvgRuntimeAllowed))
            {
                MessageBox.Show("Max Combined Average Runtime Allowed must be a number", "Invalid Entry", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            if (!double.TryParse(StartupBox.Text, out double startup))
            {
                MessageBox.Show("Startup must be a number", "Invalid Entry", MessageBoxButton.OK, MessageBoxImage.Error);
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

            if (Config.PrivilegedPlayers.TryGetValue(steamId, out PrivilegedPlayer? existingPlayer))
            {
                Config.PrivilegedPlayers[steamId] = newPlayer;
                if (Advanced_PB_Limiter.NexusInstalled)
                {
                    MessageBoxResult result = MessageBox.Show("Would you like to update this player on all Nexus instances?", "Player Updated", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (result == MessageBoxResult.Yes)
                    {
                        await NexusNetworkManager.UpdateNexusWithPrivilegedPlayerData(newPlayer);
                    }
                }
            }
            else
            {
                Config.PrivilegedPlayers.Add(steamId, newPlayer);
                if (Advanced_PB_Limiter.NexusInstalled)
                {
                    MessageBoxResult result = MessageBox.Show("Would you like to update this player on all Nexus instances?", "Player Added", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (result == MessageBoxResult.Yes)
                    {
                        await NexusNetworkManager.UpdateNexusWithPrivilegedPlayerData(newPlayer);
                    }
                }
            }
            await Save();
            PrivilegedPlayersGrid.ItemsSource = null;
            PrivilegedPlayersGrid.ItemsSource = Config.PrivilegedPlayers.Values;
        }

        private async void RemoveButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (PrivilegedPlayersGrid.SelectedIndex < 0) return;
            
            PrivilegedPlayer? selectedPlayer = (PrivilegedPlayer) PrivilegedPlayersGrid.SelectedItem;
            if (selectedPlayer is null) return;
            PrivilegedPlayersGrid.ItemsSource = null;
            Config.PrivilegedPlayers.Remove(selectedPlayer.SteamId);
            await Save();
            PrivilegedPlayersGrid.ItemsSource = Config.PrivilegedPlayers.Values;
        }

        private async void PushUserToNexus_OnClick(object sender, RoutedEventArgs e)
        {
            if (PrivilegedPlayersGrid.SelectedIndex < 0)
            {
                MessageBox.Show("No player selected", "Invalid Selection", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            PrivilegedPlayer? selectedPlayer = (PrivilegedPlayer) PrivilegedPlayersGrid.SelectedItem;
            if (selectedPlayer is null)
            {
                MessageBox.Show("Error with selected player, cannot push into Nexus.", "Unknown Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!Advanced_PB_Limiter.NexusInstalled)
            {
                MessageBox.Show("Nexus is not installed, this instance is not connected to the Nexus Controller, or this server is not online... cannot push into Nexus.", "Nexus Not Installed", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
                
            await NexusNetworkManager.UpdateNexusWithPrivilegedPlayerData(selectedPlayer);
        }
    }
}