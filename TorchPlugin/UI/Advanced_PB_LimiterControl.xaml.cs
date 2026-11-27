using System;
using System.Threading.Tasks;
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
        /// the reason is only in the Torch log. One place, so every tab says the same thing. Started from the UI thread and
        /// back on it for the message box; it logs any failure itself, so the buttons need not await it.
        /// </summary>
        internal static async Task SaveConfigAsync()
        {
            try
            {
                // Null once this instance was disposed: a tab Torch kept from before a reload.
                AdvancedPBLimiterPlugin? plugin = AdvancedPBLimiterPlugin.Instance;
                if (plugin is not null && await plugin.SaveAsync().ConfigureAwait(true)) return;
                string message;
                if (plugin is null || LimiterCore.Retired)
                    message = LimiterCore.RetiredMessage;
                else if (!plugin.IsReady)
                    message = "The config has not loaded yet, so nothing was saved. Try again in a moment.";
                else if (plugin.FailedSavesRetried)
                    message = "The config could not be saved; the Torch log says why. It is tried again every 30 seconds, and until it saves, the change lasts only until Torch restarts.";
                else
                    message = "The config could not be saved; the Torch log says why. No save is made until Torch restarts, so the change lasts only until then.";
                System.Windows.MessageBox.Show(message, "Not saved", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
            catch (Exception e)
            {
                AdvancedPBLimiterPlugin.Log.Error(e, "Saving the config from the tab failed");
            }
        }

        /// <summary>Called from the game thread when the world unloads.</summary>
        internal void OnSessionUnloading() => _reports?.OnSessionUnloading();
    }
}
