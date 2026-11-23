using System;
using System.Collections.Generic;
using System.IO;
using Advanced_PB_Limiter.Utils;
using Xunit;

namespace Advanced_PB_Limiter.Tests
{
    public sealed class KeptFileTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "apbl-keep-" + Guid.NewGuid().ToString("N"));

        public KeptFileTests()
        {
            Directory.CreateDirectory(_folder);
        }

        public void Dispose()
        {
            Directory.Delete(_folder, true);
        }

        [Fact]
        public void EachFileSetAsideGetsANameOfItsOwn()
        {
            string path = Path.Combine(_folder, "limits.cfg");
            HashSet<string> kept = new(StringComparer.OrdinalIgnoreCase);

            // More than fit in one second's name: the first is .unreadable, then the time, then the time and a number.
            for (int i = 0; i < 4; i++)
            {
                File.Create(path).Dispose(); // What is in it doesn't matter, only that each one gets a name of its own.
                string name = KeptFile.Keep(path, ".unreadable");
                Assert.True(kept.Add(name));
                Assert.False(File.Exists(path));
                Assert.EndsWith(".unreadable", name, StringComparison.Ordinal);
            }

            Assert.Contains(path + ".unreadable", kept);
            Assert.Equal(4, Directory.GetFiles(_folder, "limits.cfg*.unreadable").Length);
        }
    }
}
