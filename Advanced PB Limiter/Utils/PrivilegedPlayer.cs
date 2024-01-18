using ProtoBuf;

namespace Advanced_PB_Limiter.Utils
{
    [ProtoContract]
    public class PrivilegedPlayer
    {
        [ProtoMember(10)] public ulong SteamId { get; set; }
        [ProtoMember(20)] public string Name { get; set; }
        [ProtoMember(30)] public double RuntimeAllowance { get; set; }
        [ProtoMember(40)] public double RuntimeAverageAllowance { get; set; }
        [ProtoMember(50)] public double StartupAllowance { get; set; }
        [ProtoMember(60)] public double CombinedRuntimeAllowance { get; set; }
        [ProtoMember(70)] public double CombinedRuntimeAverageAllowance { get; set; }
        [ProtoMember(80)] public bool NoCombinedLimits { get; set; }
        [ProtoMember(90)] public Enums.Punishment Punishment { get; set; } = Enums.Punishment.TurnOff;
        [ProtoMember(100)] public int GracefulShutDownRequestDelay { get; set; } = 10;
        [ProtoMember(110)] public int OffencesBeforePunishment { get; set; } = 6;
        [ProtoMember(120)] public int DamageAmount { get; set; } = 100;
        [ProtoMember(130)] public TierData TierPlan { get; set; }

        public PrivilegedPlayer() { }
        
    }
}