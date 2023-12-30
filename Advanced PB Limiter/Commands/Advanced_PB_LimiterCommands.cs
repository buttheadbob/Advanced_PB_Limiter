using Advanced_PB_Limiter.Settings;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;

namespace Advanced_PB_Limiter.Commands
{
    [Category("Advanced_PB_Limiter")]
    public class Advanced_PB_LimiterCommands : CommandModule
    {
        public Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance.Config;

        [Command("test", "This is a Test Command.")]
        [Permission(MyPromoteLevel.Moderator)]
        public void Test()
        {
            Context.Respond("This is a Test from " + Context.Player);
        }

        [Command("testWithCommands", "This is a Test Command.")]
        [Permission(MyPromoteLevel.None)]
        public void TestWithArgs(string foo, string bar = null)
        {
            Context.Respond("This is a Test " + foo + ", " + bar);
        }
    }
}
