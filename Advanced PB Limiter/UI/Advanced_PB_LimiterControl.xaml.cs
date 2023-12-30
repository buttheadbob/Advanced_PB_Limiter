using System.Windows.Controls;
using Advanced_PB_Limiter.Settings;

namespace Advanced_PB_Limiter.UI
{
    public partial class Advanced_PB_LimiterControl : UserControl
    {
        private Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance.Config;

        public Advanced_PB_LimiterControl()
        {
            InitializeComponent();
            DataContext = Config;
        }
    }
}
