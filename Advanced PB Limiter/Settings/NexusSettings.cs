namespace Advanced_PB_Limiter.Settings
{
    public partial class Advanced_PB_LimiterConfig
    {
        private bool _AllowNexusConfigUpdates = true;
        public bool AllowNexusConfigUpdates { get => _AllowNexusConfigUpdates; set => SetValue(ref _AllowNexusConfigUpdates, value); }
        
        private bool _AllowNexusPrivilegedPlayerUpdates = true;
        public bool AllowNexusPrivilegedPlayerUpdates { get => _AllowNexusPrivilegedPlayerUpdates; set => SetValue(ref _AllowNexusPrivilegedPlayerUpdates, value); }
    }
}