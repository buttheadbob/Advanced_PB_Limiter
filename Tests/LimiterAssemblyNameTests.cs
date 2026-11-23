using Advanced_PB_Limiter.Utils;
using Xunit;

namespace Advanced_PB_Limiter.Tests
{
    /// <summary>The Quasar companion finds the limiter's assembly as a DLL build names it and as Magnetar names a hub install, and never a script's.</summary>
    public sealed class LimiterAssemblyNameTests
    {
        [Theory]
        [InlineData("AdvancedPBLimiter")]
        [InlineData("Advanced PB Limiter-1a2b3c4d_k3j9x0")]
        [InlineData("Advanced PB Limiter_1a2b3c4d")]
        public void TheLimitersAssemblyIsFound(string name) => Assert.True(LimiterAssemblyName.Is(name));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Advanced PB Limiter")]
        [InlineData("Advanced PB LimiterX")]
        [InlineData("AdvancedPBLimiter.Quasar")]
        [InlineData("AdvancedPBLimiter.QuasarCompanion")]
        [InlineData("118023450981234567-Advanced PB Limiter")]
        [InlineData("118023450981234567-AdvancedPBLimiter")]
        public void OtherAssembliesAreNot(string? name) => Assert.False(LimiterAssemblyName.Is(name));

        /// <summary>Magnetar names a hub install after the manifest's FriendlyName, so the two must stay the same.</summary>
        [Fact]
        public void TheManifestsFriendlyNameIsTheOneLookedFor()
        {
            // Copied next to the tests by Tests.csproj.
            string manifest = System.IO.Path.Combine(System.AppContext.BaseDirectory, "AdvancedPBLimiter.xml");
            string friendlyName = System.Xml.Linq.XDocument.Load(manifest).Root!.Element("FriendlyName")!.Value.Trim();
            Assert.True(LimiterAssemblyName.Is(friendlyName + "-1a2b3c4d_k3j9x0"), friendlyName);
        }
    }
}
