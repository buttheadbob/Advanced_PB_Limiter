using Advanced_PB_Limiter.QuasarContracts;
using Xunit;

namespace Advanced_PB_Limiter.Tests
{
    public sealed class ServerPickKeysTests
    {
        [Fact]
        public void AClusterKeyNamesTheSlotNotTheIncarnation()
        {
            string first = ServerPickKeys.For("cluster-1", "node-a", "server-1");
            string second = ServerPickKeys.For("cluster-1", "node-a", "server-2");

            Assert.Equal(first, second);
            Assert.True(ServerPickKeys.TryCluster(first, out string clusterId, out string slot));
            Assert.Equal("cluster-1", clusterId);
            Assert.Equal("node-a", slot);
        }

        [Fact]
        public void AServerKeyNamesTheId()
        {
            string key = ServerPickKeys.For("", "", "abc/def | ghi");

            Assert.Equal("abc/def | ghi", ServerPickKeys.ServerIdOf(key));
            Assert.False(ServerPickKeys.TryCluster(key, out _, out _));
        }

        [Fact]
        public void SpecialCharactersSurviveTheClusterKey()
        {
            string key = ServerPickKeys.For("a|b", "c/d e", "x");

            Assert.True(ServerPickKeys.TryCluster(key, out string clusterId, out string slot));
            Assert.Equal("a|b", clusterId);
            Assert.Equal("c/d e", slot);
        }
    }
}
