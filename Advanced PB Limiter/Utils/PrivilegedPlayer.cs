namespace Advanced_PB_Limiter.Utils
{
    public class PrivilegedPlayer
    {
        public ulong SteamId { get; set; }
        public string Name { get; set; }
        public double RuntimeAllowance { get; set; }
        public double RuntimeAverageAllowance { get; set; }
        public double StartupAllowance { get; set; }
        public double CombinedRuntimeAllowance { get; set; }
        public double CombinedRuntimeAverageAllowance { get; set; }
        public bool NoCombinedLimits { get; set; }
        public Enums.Punishment Punishment { get; set; } = Enums.Punishment.TurnOff;
        public int GracefulShutDownRequestDelay { get; set; } = 10;
        public int OffencesBeforePunishment { get; set; } = 6;

        public PrivilegedPlayer() { }
    }
}