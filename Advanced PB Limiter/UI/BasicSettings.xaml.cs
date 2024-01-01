using System.Threading.Tasks;
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
    }
}