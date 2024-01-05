using System.Threading.Tasks;
using Advanced_PB_Limiter.Settings;

namespace Advanced_PB_Limiter.UI
{
    public partial class Advanced_PB_LimiterControl
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;

        public Advanced_PB_LimiterControl()
        {
            InitializeComponent();
            DataContext = Config;

            MainSettingsTab.Content = new BasicSettings();
            PrivilegedUsersTab.Content = new PrivilegedUsers();
            ReportsTab.Content = new Reports();
            NexusTab.Content = new NexusSettings();
        }
        
        private async Task Save()
        {
            await Advanced_PB_Limiter.Instance!.Save();
        }
    }
}
