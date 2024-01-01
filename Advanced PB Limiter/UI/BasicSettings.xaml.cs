using System.Windows.Controls;
using Advanced_PB_Limiter.Settings;

namespace Advanced_PB_Limiter.UI
{
    public partial class BasicSettings : UserControl
    {
        private Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance.Config;
        
        public BasicSettings()
        {
            InitializeComponent();
            DataContext = Config;
        }
    }
}