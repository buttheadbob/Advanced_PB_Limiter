namespace Advanced_PB_Limiter.Utils
{
    public class PrivilegedPlayer
    {
        public string SteamId { get; set; }
        public double FrameRuntimeAllowance { get; set; }
        public double StartupAllowance { get; set; }
        public double CombinedRuntimeAllowance { get; set; }
    }
}