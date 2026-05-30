using System;
using Sandbox.Game.Entities.Blocks;
using Sandbox.ModAPI;

namespace Advanced_PB_Limiter.Utils
{
    public class ProgramInfo
    {
        public WeakReference<IMyGridProgram>? ProgramReference { get; set; }
        public DateTime LastUpdate { get; set; }
        public int IsChecking;
        public long OwnerId { get; set; }
        public MyProgrammableBlock? Pb { get; set; }
    }
}