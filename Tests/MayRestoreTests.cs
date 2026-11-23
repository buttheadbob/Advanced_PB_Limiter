using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Advanced_PB_Limiter.Utils;
using Xunit;

namespace Advanced_PB_Limiter.Tests
{
    public sealed class MayRestoreTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "apbl-restore-" + Guid.NewGuid().ToString("N"));

        public MayRestoreTests()
        {
            Directory.CreateDirectory(_folder);
        }

        public void Dispose()
        {
            Directory.Delete(_folder, true);
        }

        private async Task<string> CopyAsync(string name, string text)
        {
            string path = Path.Combine(_folder, name);
            byte[] bytes = Encoding.UTF8.GetBytes(text);
#if NETFRAMEWORK
            using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write))
                await stream.WriteAsync(bytes, 0, bytes.Length);
#else
            await File.WriteAllBytesAsync(path, bytes);
#endif
            return path;
        }

        [Fact]
        public async Task AWholeCopyMayRestore()
        {
            Assert.True(SafeFile.MayRestore(await CopyAsync("whole.cfg.damage.tmp", "<LimiterConfig><Enabled>true</Enabled></LimiterConfig>"), SafeFile.IsWholeXml));
        }

        [Fact]
        public async Task ACopyCutShortNeverRestores()
        {
            // It would otherwise hold saves back at every start, for a restore that can't happen.
            Assert.False(SafeFile.MayRestore(await CopyAsync("cut.cfg.damage.tmp", "<LimiterConfig><Enab"), SafeFile.IsWholeXml));
        }

        [Fact]
        public void NoCopyRestoresNothing()
        {
            Assert.False(SafeFile.MayRestore(Path.Combine(_folder, "none.cfg.damage.tmp"), SafeFile.IsWholeXml));
        }

        [Fact]
        public async Task ACopyThatCannotBeReadMayRestore()
        {
            // Only Windows refuses to read a file another program holds open without sharing.
            if (Path.DirectorySeparatorChar != '\\') return;
            string path = await CopyAsync("held.cfg.damage.tmp", "<LimiterConfig><Enab");
#if NETFRAMEWORK
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
#else
            await using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
#endif
                Assert.True(SafeFile.MayRestore(path, SafeFile.IsWholeXml));
        }
    }
}
