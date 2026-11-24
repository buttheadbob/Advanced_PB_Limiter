using System;
using Advanced_PB_Limiter.Settings;

namespace Advanced_PB_Limiter.UI
{
    public partial class Advanced_PB_LimiterControl
    {
        private static Advanced_PB_LimiterConfig Config => AdvancedPBLimiterPlugin.Instance!.Config!;
        private Reports? _reports;

        public Advanced_PB_LimiterControl()
        {
            InitializeComponent();
            DataContext = Config;
            AddTabs();
            ShowStatus();
        }

        /// <summary>What the header says about this instance. The version comes from the host, not a copied constant.</summary>
        private void ShowStatus()
        {
            try
            {
                StatusText.Text = "v" + LimiterCore.Host.Version;
                StatusDetailText.Text = LimiterCore.Retired ? "Retired, restart Torch" : "Torch";
            }
            catch (InvalidOperationException)
            {
                // The header is cosmetic; a plugin still initializing must not fail to draw because of it.
                StatusText.Text = "Advanced PB Limiter";
            }
        }

        private void AddTabs()
        {
            MainSettingsTab.Content = new BasicSettings();
            PrivilegedUsersTab.Content = new PrivilegedUsers();
            _reports = new Reports();
            ReportsTab.Content = _reports;
        }

        /// <summary>
        /// Saves the config for a button on one of the tabs, and tells the admin in a message box when it was not saved, as
        /// the reason is only in the Torch log. One place, so every tab says the same thing.
        /// </summary>
        internal static void SaveConfig()
        {
            AdvancedPBLimiterPlugin plugin = AdvancedPBLimiterPlugin.Instance!;
            if (plugin.Save()) return;
            string message;
            if (LimiterCore.Retired)
                message = LimiterCore.RetiredMessage;
            else if (plugin.FailedSavesRetried)
                message = "The config could not be saved; the Torch log says why. It is tried again every 30 seconds, and until it saves, the change lasts only until Torch restarts.";
            else
                message = "The config could not be saved; the Torch log says why. No save is made until Torch restarts, so the change lasts only until then.";
            System.Windows.MessageBox.Show(message, "Not saved", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }

        /// <summary>Called from the game thread when the world unloads.</summary>
        internal void OnSessionUnloading() => _reports?.OnSessionUnloading();
    }
}
