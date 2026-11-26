using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Advanced_PB_Limiter.Utils;
using Xunit;

namespace Advanced_PB_Limiter.Tests
{
    public sealed class InstantPunishmentFileTests : IDisposable
    {
        private static readonly string[] BothSettings = { "InstantKillPunishment", "MemoryPunishment" };
        private static readonly string[] MemoryOnly = { "MemoryPunishment" };
        private static readonly string[] KillOnly = { "InstantKillPunishment" };

        private readonly string _folder = Path.Combine(Path.GetTempPath(), "apbl-tests-" + Guid.NewGuid().ToString("N"));

        public InstantPunishmentFileTests()
        {
            Directory.CreateDirectory(_folder);
        }

        public void Dispose()
        {
            Directory.Delete(_folder, recursive: true);
        }

        private static string Config(string newLine, string instantKill, string memory)
        {
            return string.Join(newLine, new[]
            {
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>",
                "<LimiterConfig>",
                "  <!-- <InstantKillPunishment>TurnOff</InstantKillPunishment> -->",
                "  <MaxRunTimeMS>1.25</MaxRunTimeMS>",
                "  <InstantKillPunishment>" + instantKill + "</InstantKillPunishment>",
                "    <Punishment>Damage</Punishment>",
                "  <MemoryPunishment>" + memory + "</MemoryPunishment>",
                "</LimiterConfig>",
                "",
            });
        }

        private async Task<string> WriteAsync(string name, byte[] content)
        {
            string path = Path.Combine(_folder, name);
#if NETFRAMEWORK
            using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write))
                await stream.WriteAsync(content, 0, content.Length);
#else
            await File.WriteAllBytesAsync(path, content);
#endif
            return path;
        }

#if NETFRAMEWORK
        private static async Task<byte[]> ReadAsync(string path)
        {
            using (FileStream stream = new(path, FileMode.Open, FileAccess.Read))
            {
                byte[] content = new byte[stream.Length];
                int read = 0;
                while (read < content.Length)
                    read += await stream.ReadAsync(content, read, content.Length - read);
                return content;
            }
        }
#else
        private static Task<byte[]> ReadAsync(string path) => File.ReadAllBytesAsync(path);
#endif

        private static byte[] Bytes(bool bom, string text)
        {
            byte[] body = Encoding.UTF8.GetBytes(text);
            return bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray() : body;
        }

        private static async Task<List<string>> ReplaceDamageAsync(string path) =>
            (await InstantPunishmentFile.ReplaceDamageAsync(path)).Changed;

        private static async Task<(List<string> Changed, string? Recovered)> ReplaceDamageWithRecoveryAsync(string path)
        {
            InstantPunishmentFile.ReplaceDamageResult result = await InstantPunishmentFile.ReplaceDamageAsync(path);
            return (result.Changed, result.Recovered);
        }

        private static async Task<(List<string> Changed, string? Recovered, bool CopyWaiting)> ReplaceDamageHeldAsync(string path)
        {
            InstantPunishmentFile.ReplaceDamageResult result = await InstantPunishmentFile.ReplaceDamageAsync(path, moveConfigAside: false);
            return (result.Changed, result.Recovered, result.CopyWaiting);
        }

        private static async Task<string?> WriteWithSafeFileAsync(string path, string temp, byte[] bytes)
        {
            using MemoryStream content = new(bytes);
            SafeFile.WriteResult write = await SafeFile.WriteAsync(path, temp, content, SafeFile.IsWholeXml);
            return write.Kept;
        }

        [Theory]
        [InlineData(true, "\r\n")]
        [InlineData(false, "\r\n")]
        [InlineData(true, "\n")]
        [InlineData(false, "\n")]
        public async Task DamageIsRewrittenAndNothingElseChanges(bool bom, string newLine)
        {
            string path = await WriteAsync("damage.cfg", Bytes(bom, Config(newLine, "Damage", "Damage")));

            List<string> changed = await ReplaceDamageAsync(path);

            Assert.Equal(BothSettings, changed, StringComparer.Ordinal);
            Assert.Equal(Bytes(bom, Config(newLine, "TurnOff", "TurnOff")), await ReadAsync(path));
            Assert.False(File.Exists(path + ".damage.tmp"));
        }

        [Fact]
        public async Task MixedLineEndingsAndNoDeclarationStayAsTheyWere()
        {
            const string before = "<LimiterConfig>\r\n  <InstantKillPunishment>Damage</InstantKillPunishment>\n  <MemoryPunishment>Destroy</MemoryPunishment>\r\n</LimiterConfig>";
            string path = await WriteAsync("mixed.cfg", Bytes(false, before));

            await ReplaceDamageAsync(path);

            const string after = "<LimiterConfig>\r\n  <InstantKillPunishment>TurnOff</InstantKillPunishment>\n  <MemoryPunishment>Destroy</MemoryPunishment>\r\n</LimiterConfig>";
            Assert.Equal(Bytes(false, after), await ReadAsync(path));
        }

        [Fact]
        public async Task DamageInsideACommentIsLeftAloneAndSpacesAroundItGo()
        {
            const string before = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<LimiterConfig>\n  <!-- <MemoryPunishment>Damage</MemoryPunishment> -->\n  <MemoryPunishment> Damage </MemoryPunishment>\n</LimiterConfig>\n";
            string path = await WriteAsync("comment.cfg", Bytes(true, before));

            Assert.Equal(MemoryOnly, await ReplaceDamageAsync(path), StringComparer.Ordinal);
            const string after = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<LimiterConfig>\n  <!-- <MemoryPunishment>Damage</MemoryPunishment> -->\n  <MemoryPunishment>TurnOff</MemoryPunishment>\n</LimiterConfig>\n";
            Assert.Equal(Bytes(true, after), await ReadAsync(path));
        }

        [Fact]
        public async Task OnlyTheSettingThatHeldDamageIsReported()
        {
            string path = await WriteAsync("one.cfg", Bytes(false, Config("\n", "Destroy", "Damage")));

            Assert.Equal(MemoryOnly, await ReplaceDamageAsync(path), StringComparer.Ordinal);
            Assert.Equal(Bytes(false, Config("\n", "Destroy", "TurnOff")), await ReadAsync(path));
        }

        [Fact]
        public async Task AUtf16FileIsRewrittenInUtf16()
        {
            const string before = "<?xml version=\"1.0\" encoding=\"utf-16\"?>\r\n<LimiterConfig>\r\n  <MaxRunTimeMS>1.25</MaxRunTimeMS>\r\n  <InstantKillPunishment>Damage</InstantKillPunishment>\r\n  <MemoryPunishment>Destroy</MemoryPunishment>\r\n</LimiterConfig>\r\n";
            byte[] bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(before)).ToArray();
            string path = await WriteAsync("unicode.cfg", bytes);

            Assert.Equal(KillOnly, await ReplaceDamageAsync(path), StringComparer.Ordinal);
            byte[] written = await ReadAsync(path);
            Assert.Equal(Encoding.Unicode.GetPreamble(), written.Take(2).ToArray());
            System.Xml.Linq.XDocument after = System.Xml.Linq.XDocument.Parse(Encoding.Unicode.GetString(written, 2, written.Length - 2));
            Assert.Equal("TurnOff", after.Root?.Element("InstantKillPunishment")?.Value);
            Assert.Equal("Destroy", after.Root?.Element("MemoryPunishment")?.Value);
            Assert.Equal("1.25", after.Root?.Element("MaxRunTimeMS")?.Value);
        }

        [Fact]
        public async Task ARestoreThatCannotWriteLeavesTheFileAndTheCopy()
        {
            // Only Windows refuses to rename or write a file another program holds open without sharing.
            if (Path.DirectorySeparatorChar != '\\') return;
            byte[] cut = Bytes(false, "<LimiterConfig><Instant");
            string path = await WriteAsync("held-restore.cfg", cut);
            string temp = await WriteAsync("held-restore.cfg.damage.tmp", Bytes(false, Config("\n", "Destroy", "Destroy")));

            IOException error;
#if NETFRAMEWORK
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                error = await Assert.ThrowsAsync<IOException>(async () => await InstantPunishmentFile.ReplaceDamageAsync(path));
#else
            await using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                error = await Assert.ThrowsAsync<IOException>(async () => await InstantPunishmentFile.ReplaceDamageAsync(path));
#endif

            Assert.Contains("could not be restored", error.Message, StringComparison.Ordinal);
            Assert.Contains("The copy is kept for the next start.", error.Message, StringComparison.Ordinal);
            Assert.Equal(cut, await ReadAsync(path));
            Assert.Equal(Bytes(false, Config("\n", "Destroy", "Destroy")), await ReadAsync(temp));
        }

        [Fact]
        public async Task ALineBreakInAValueSurvivesTheRewrite()
        {
            // A value holding CR and LF as character references, as a file edited by hand or by another tool may. Written
            // back as literal characters, reading the file again would turn CR LF into LF and change the value.
            const string before = "<?xml version=\"1.0\" encoding=\"utf-16\"?>\r\n<LimiterConfig>\r\n  <Name>a&#xD;&#xA;b</Name>\r\n  <InstantKillPunishment>Damage</InstantKillPunishment>\r\n</LimiterConfig>\r\n";
            byte[] bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(before)).ToArray();
            string path = await WriteAsync("linebreak.cfg", bytes);

            Assert.Equal(KillOnly, await ReplaceDamageAsync(path), StringComparer.Ordinal);
            byte[] written = await ReadAsync(path);
            System.Xml.Linq.XDocument after;
#if NETFRAMEWORK
            using (MemoryStream read = new(written))
#else
            await using (MemoryStream read = new(written))
#endif
            using (System.Xml.XmlReader reader = SafeXml.Reader(read))
                after = System.Xml.Linq.XDocument.Load(reader);
            Assert.Equal("a\r\nb", after.Root?.Element("Name")?.Value);
            Assert.Equal("TurnOff", after.Root?.Element("InstantKillPunishment")?.Value);
        }

        [Fact]
        public async Task AFileDeclaredInLatin1IsRewrittenInLatin1()
        {
            Encoding latin1 = Encoding.GetEncoding("iso-8859-1");
            const string before = "<?xml version=\"1.0\" encoding=\"iso-8859-1\"?>\n<LimiterConfig>\n  <!-- caf\u00e9 -->\n  <InstantKillPunishment>Damage</InstantKillPunishment>\n</LimiterConfig>\n";
            string path = await WriteAsync("latin1.cfg", latin1.GetBytes(before));

            Assert.Equal(KillOnly, await ReplaceDamageAsync(path), StringComparer.Ordinal);
            byte[] written = await ReadAsync(path);
            Assert.Contains((byte)0xE9, written);
            string text = latin1.GetString(written);
            System.Xml.Linq.XDocument after = System.Xml.Linq.XDocument.Parse(text);
            Assert.Equal("TurnOff", after.Root?.Element("InstantKillPunishment")?.Value);
            Assert.Contains("caf\u00e9", text, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("<![CDATA[Damage]]>")]
        [InlineData("&#68;amage")]
        public async Task AValueTheTextEditCannotReachIsStillReplaced(string value)
        {
            string path = await WriteAsync("odd.cfg", Bytes(false, Config("\n", value, "Destroy")));

            Assert.Equal(KillOnly, await ReplaceDamageAsync(path), StringComparer.Ordinal);
            System.Xml.Linq.XDocument after = System.Xml.Linq.XDocument.Parse(Encoding.UTF8.GetString(await ReadAsync(path)));
            Assert.Equal("TurnOff", after.Root?.Element("InstantKillPunishment")?.Value);
            Assert.Equal("Destroy", after.Root?.Element("MemoryPunishment")?.Value);
        }

        [Fact]
        public async Task AFileWithoutDamageIsLeftAlone()
        {
            byte[] content = Bytes(true, Config("\r\n", "Destroy", "TurnOff"));
            string path = await WriteAsync("clean.cfg", content);
            DateTime written = File.GetLastWriteTimeUtc(path);

            Assert.Empty(await ReplaceDamageAsync(path));
            Assert.Equal(content, await ReadAsync(path));
            Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        }

        [Fact]
        public async Task ASecondCheckChangesNothing()
        {
            string path = await WriteAsync("twice.cfg", Bytes(false, Config("\n", "Damage", "Damage")));
            await ReplaceDamageAsync(path);

            Assert.Empty(await ReplaceDamageAsync(path));
        }

        [Fact]
        public async Task AMissingFileIsLeftAlone()
        {
            string path = Path.Combine(_folder, "none.cfg");

            Assert.Empty(await ReplaceDamageAsync(path));
            Assert.False(File.Exists(path));
        }

        [Fact]
        public async Task AFileWithADocumentTypeIsRefused()
        {
            const string text = "<?xml version=\"1.0\"?>\n<!DOCTYPE LimiterConfig [<!ENTITY x \"Damage\">]>\n<LimiterConfig><InstantKillPunishment>&x;</InstantKillPunishment></LimiterConfig>\n";
            string path = await WriteAsync("dtd.cfg", Bytes(false, text));

            await Assert.ThrowsAnyAsync<System.Xml.XmlException>(async () => await InstantPunishmentFile.ReplaceDamageAsync(path));
            Assert.Equal(Bytes(false, text), await ReadAsync(path));
        }

        [Fact]
        public async Task ASameNamedElementDeeperInIsNotTheOneChanged()
        {
            const string before = "<LimiterConfig>\n  <Old><InstantKillPunishment>Damage</InstantKillPunishment></Old>\n  <InstantKillPunishment>Damage</InstantKillPunishment>\n</LimiterConfig>";
            string path = await WriteAsync("nested.cfg", Bytes(false, before));

            Assert.Equal(KillOnly, await ReplaceDamageAsync(path), StringComparer.Ordinal);

            System.Xml.Linq.XElement root = System.Xml.Linq.XDocument.Parse(Encoding.UTF8.GetString(await ReadAsync(path))).Root!;
            Assert.Equal("TurnOff", root.Element("InstantKillPunishment")!.Value);
            Assert.Equal("Damage", root.Element("Old")!.Element("InstantKillPunishment")!.Value);
        }

        [Fact]
        public async Task AConfigCutShortByAnInterruptedWriteIsRestoredFromTheFileBesideIt()
        {
            string complete = Config("\n", "TurnOff", "Destroy");
            string path = await WriteAsync("cut.cfg", Bytes(false, complete.Substring(0, complete.Length / 2)));
            await WriteAsync("cut.cfg.damage.tmp", Bytes(false, complete));

            Assert.Empty(await ReplaceDamageAsync(path));

            Assert.Equal(Bytes(false, complete), await ReadAsync(path));
            Assert.False(File.Exists(path + ".damage.tmp"));
            // In case it was a hand edit, not a write cut short, the file that was there is kept.
            Assert.Equal(Bytes(false, complete.Substring(0, complete.Length / 2)), await ReadAsync(path + ".before-restore"));
        }

        [Fact]
        public async Task AWriteKeepsADifferingCompleteCopyWhileTheFileIsNotWhole()
        {
            string whole = Config("\n", "TurnOff", "TurnOff");
            string path = await WriteAsync("earlier.cfg", Bytes(false, whole.Substring(0, whole.Length / 2)));
            string temp = await WriteAsync("earlier.cfg.tmp", Bytes(false, Config("\n", "Destroy", "Destroy")));

            string? kept = await WriteWithSafeFileAsync(path, temp, Bytes(false, Config("\n", "TurnOff", "Destroy")));

            Assert.Equal(temp + ".kept", kept);
            Assert.Equal(Bytes(false, Config("\n", "Destroy", "Destroy")), await ReadAsync(temp + ".kept"));
            Assert.Equal(Bytes(false, Config("\n", "TurnOff", "Destroy")), await ReadAsync(path));
            Assert.False(File.Exists(temp));
        }

        [Fact]
        public async Task AWriteReplacesALeftoverCopyBesideAWholeFile()
        {
            // An earlier write that never landed, beside a whole file: the newer content replaces it, so failed saves while
            // another program holds the file don't pile up copies.
            string path = await WriteAsync("same-left.cfg", Bytes(false, Config("\n", "TurnOff", "TurnOff")));
            string temp = await WriteAsync("same-left.cfg.tmp", Bytes(false, Config("\n", "Destroy", "Destroy")));

            string? kept = await WriteWithSafeFileAsync(path, temp, Bytes(false, Config("\n", "TurnOff", "Destroy")));

            Assert.Null(kept);
            Assert.Equal(Bytes(false, Config("\n", "TurnOff", "Destroy")), await ReadAsync(path));
            Assert.Equal("same-left.cfg", Path.GetFileName(Assert.Single(Directory.GetFiles(_folder, "same-left.cfg*"))));
        }

        [Fact]
        public async Task ALeftoverFileBesideAReadableConfigIsRemoved()
        {
            string path = await WriteAsync("left.cfg", Bytes(false, Config("\n", "Damage", "Destroy")));
            await WriteAsync("left.cfg.damage.tmp", Bytes(false, "<LimiterConfig><Instant"));

            Assert.Equal(KillOnly, await ReplaceDamageAsync(path), StringComparer.Ordinal);

            Assert.Equal(Bytes(false, Config("\n", "TurnOff", "Destroy")), await ReadAsync(path));
            Assert.False(File.Exists(path + ".damage.tmp"));
        }

        [Theory]
        [InlineData(FileShare.Read | FileShare.Delete)]
        [InlineData(FileShare.Read)]
        public async Task ACutShortConfigIsRestoredWhileAnotherProgramReadsTheCopy(FileShare share)
        {
            string complete = Config("\n", "TurnOff", "Destroy");
            string path = await WriteAsync("held.cfg", Bytes(false, complete.Substring(0, complete.Length / 2)));
            string temp = await WriteAsync("held.cfg.damage.tmp", Bytes(false, complete));

            List<string> changed;
            string? recovered;
#if NETFRAMEWORK
            using (new FileStream(temp, FileMode.Open, FileAccess.Read, share))
                (changed, recovered) = await ReplaceDamageWithRecoveryAsync(path);
#else
            await using (new FileStream(temp, FileMode.Open, FileAccess.Read, share))
                (changed, recovered) = await ReplaceDamageWithRecoveryAsync(path);
#endif

            Assert.Empty(changed);
            Assert.Equal(Bytes(false, complete), await ReadAsync(path));
            Assert.StartsWith(path + " was not a whole file and was restored", recovered, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ACompleteCopyThatDiffersFromACompleteConfigIsKept()
        {
            string path = await WriteAsync("kept.cfg", Bytes(false, Config("\n", "TurnOff", "TurnOff")));
            string temp = await WriteAsync("kept.cfg.damage.tmp", Bytes(false, Config("\n", "TurnOff", "Destroy")));

            (List<string> changed, string? recovered) = await ReplaceDamageWithRecoveryAsync(path);
            Assert.Empty(changed);

            Assert.Equal(Bytes(false, Config("\n", "TurnOff", "TurnOff")), await ReadAsync(path));
            Assert.False(File.Exists(temp));
            Assert.Equal(Bytes(false, Config("\n", "TurnOff", "Destroy")), await ReadAsync(temp + ".kept"));
            Assert.StartsWith("A complete copy of " + path, recovered, StringComparison.Ordinal);
            Assert.Contains(temp + ".kept", recovered, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ARewriteInterruptedBeforeItsSwapIsDoneAgainQuietly()
        {
            string path = await WriteAsync("again.cfg", Bytes(false, Config("\n", "Damage", "Destroy")));
            string temp = await WriteAsync("again.cfg.damage.tmp", Bytes(false, Config("\n", "TurnOff", "Destroy")));

            (List<string> changed, string? recovered) = await ReplaceDamageWithRecoveryAsync(path);
            Assert.Equal(KillOnly, changed, StringComparer.Ordinal);

            Assert.Equal(Bytes(false, Config("\n", "TurnOff", "Destroy")), await ReadAsync(path));
            Assert.Null(recovered);
            Assert.Equal("again.cfg", Path.GetFileName(Assert.Single(Directory.GetFiles(_folder, "again.cfg*"))));
        }

        [Fact]
        public async Task AKeptCopyWithOtherSettingsSurvivesTheNextRewrite()
        {
            string path = await WriteAsync("survive.cfg", Bytes(false, Config("\n", "Damage", "TurnOff")));
            string temp = await WriteAsync("survive.cfg.damage.tmp", Bytes(false, Config("\n", "Destroy", "Destroy")));

            (List<string> changed, string? recovered) = await ReplaceDamageWithRecoveryAsync(path);
            Assert.Equal(KillOnly, changed, StringComparer.Ordinal);

            Assert.Equal(Bytes(false, Config("\n", "TurnOff", "TurnOff")), await ReadAsync(path));
            Assert.Equal(Bytes(false, Config("\n", "Destroy", "Destroy")), await ReadAsync(temp + ".kept"));
            Assert.Contains(temp + ".kept", recovered, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AMissingConfigIsRestoredFromACompleteCopy()
        {
            string path = Path.Combine(_folder, "gone.cfg");
            string temp = await WriteAsync("gone.cfg.damage.tmp", Bytes(false, Config("\n", "TurnOff", "Destroy")));

            (List<string> changed, string? recovered) = await ReplaceDamageWithRecoveryAsync(path);
            Assert.Empty(changed);

            Assert.Equal(Bytes(false, Config("\n", "TurnOff", "Destroy")), await ReadAsync(path));
            Assert.False(File.Exists(temp));
            Assert.StartsWith(path + " was missing or cut short", recovered, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ACopyThatCannotBeMovedAsideIsNotOverwrittenByTheRewrite()
        {
            // Only Windows refuses to rename a file another program holds open without sharing delete.
            if (Path.DirectorySeparatorChar != '\\') return;
            string path = await WriteAsync("stuck.cfg", Bytes(false, Config("\n", "Damage", "TurnOff")));
            string temp = await WriteAsync("stuck.cfg.damage.tmp", Bytes(false, Config("\n", "Destroy", "Destroy")));

            List<string> changed;
            string? recovered;
            bool copyWaiting;
#if NETFRAMEWORK
            using (new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read))
                (changed, recovered, copyWaiting) = await ReplaceDamageHeldAsync(path);
#else
            await using (new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read))
                (changed, recovered, copyWaiting) = await ReplaceDamageHeldAsync(path);
#endif

            Assert.Empty(changed);
            Assert.Equal(Bytes(false, Config("\n", "Damage", "TurnOff")), await ReadAsync(path));
            Assert.Equal(Bytes(false, Config("\n", "Destroy", "Destroy")), await ReadAsync(temp));
            Assert.Contains("was not rewritten", recovered, StringComparison.Ordinal);
            Assert.True(copyWaiting); // Magnetar then saves nothing until the restart, so the copy becomes the config.

            // That restart, with the config set aside as unreadable meanwhile: the copy is what is read.
            File.Move(path, path + ".unreadable");
            InstantPunishmentFile.ReplaceDamageResult again = await InstantPunishmentFile.ReplaceDamageAsync(path, moveConfigAside: false);
            Assert.Empty(again.Changed);
            Assert.False(again.CopyWaiting);
            Assert.Equal(Bytes(false, Config("\n", "Destroy", "Destroy")), await ReadAsync(path));
        }

        [Fact]
        public async Task TheTorchOverloadMovesTheConfigAsideForAWaitingCopy()
        {
            // Only Windows refuses to rename a file another program holds open without sharing delete.
            if (Path.DirectorySeparatorChar != '\\') return;
            string path = await WriteAsync("torch-stuck.cfg", Bytes(false, Config("\n", "Damage", "TurnOff")));
            string temp = await WriteAsync("torch-stuck.cfg.damage.tmp", Bytes(false, Config("\n", "Destroy", "Destroy")));

            string? recovered;
#if NETFRAMEWORK
            using (new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                InstantPunishmentFile.ReplaceDamageResult result = await InstantPunishmentFile.ReplaceDamageAsync(path);
                Assert.Empty(result.Changed);
                recovered = result.Recovered;
            }
#else
            await using (new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                InstantPunishmentFile.ReplaceDamageResult result = await InstantPunishmentFile.ReplaceDamageAsync(path);
                Assert.Empty(result.Changed);
                recovered = result.Recovered;
            }
#endif

            Assert.False(File.Exists(path));
            string aside = Assert.Single(Directory.GetFiles(_folder, "torch-stuck.cfg.unreadable*"));
            Assert.Equal(Bytes(false, Config("\n", "Damage", "TurnOff")), await ReadAsync(aside));
            Assert.Equal(Bytes(false, Config("\n", "Destroy", "Destroy")), await ReadAsync(temp));
            Assert.Contains("moved aside", recovered, StringComparison.Ordinal);
            Assert.Contains("nothing is saved", recovered, StringComparison.Ordinal);

            // The next start restores the copy; it holds no Damage, so it is read as it is.
            Assert.Empty((await ReplaceDamageWithRecoveryAsync(path)).Changed);
            Assert.Equal(Bytes(false, Config("\n", "Destroy", "Destroy")), await ReadAsync(path));
            Assert.False(File.Exists(temp));
        }

        [Fact]
        public async Task NoCopyWaitsAfterAnOrdinaryRewrite()
        {
            string path = await WriteAsync("plain.cfg", Bytes(false, Config("\n", "Damage", "TurnOff")));

            InstantPunishmentFile.ReplaceDamageResult plain = await InstantPunishmentFile.ReplaceDamageAsync(path, moveConfigAside: false);
            Assert.Equal(KillOnly, plain.Changed, StringComparer.Ordinal);

            Assert.False(plain.CopyWaiting);
            Assert.False(File.Exists(path + ".damage.tmp"));
        }

        [Fact]
        public async Task WritesOfOneFileFromManyThreadsNeverCollide()
        {
            string path = await WriteAsync("busy.cfg", Bytes(false, Config("\n", "TurnOff", "TurnOff")));
            string[] versions = Enumerable.Range(0, 8).Select(i => Config("\n", "TurnOff", "TurnOff") + "<!-- " + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + " -->\n").ToArray();

            await Task.WhenAll(versions.Select(version => Task.Run(async () =>
            {
                for (int round = 0; round < 50; round++)
                {
                    using MemoryStream content = new(Bytes(false, version));
                    await SafeFile.WriteAsync(path, path + ".tmp", content, SafeFile.IsWholeXml);
                }
            })));

            string final = Encoding.UTF8.GetString(await ReadAsync(path));
            Assert.Contains(final, versions, StringComparer.Ordinal);
            Assert.False(File.Exists(path + ".tmp"));
        }

        [Fact]
        public async Task AWriteWaitsWhileAnotherHolderHasTheFile()
        {
            string path = await WriteAsync("held-lock.cfg", Bytes(false, Config("\n", "TurnOff", "TurnOff")));
            using SemaphoreSlim held = new(0);
            using ManualResetEventSlim release = new(false);

            // The lock is a lock file, so a holder on another thread, or in another copy of the assembly, is waited for.
            // The holder keeps the file open while the writer waits, and the OS drops the lock when it closes.
            Task holder = Task.Run(async () =>
            {
                using (await SafeFile.HoldAsync(path))
                {
                    held.Release();
                    release.Wait();
                }
            });
            await held.WaitAsync();
            Task writer = Task.Run(async () =>
            {
                using MemoryStream content = new(Bytes(false, Config("\n", "Destroy", "Destroy")));
                await SafeFile.WriteAsync(path, path + ".tmp", content, SafeFile.IsWholeXml);
            });

            Task first = await Task.WhenAny(writer, Task.Delay(300));
            Assert.NotSame(writer, first);
            release.Set();
            await Task.WhenAll(holder, writer);
            Assert.Equal(Bytes(false, Config("\n", "Destroy", "Destroy")), await ReadAsync(path));
        }

        [Fact]
        public async Task AKeptNameAlreadyTakenGetsAnotherAndTheConfigIsStillRead()
        {
            string path = await WriteAsync("taken.cfg", Bytes(false, Config("\n", "TurnOff", "TurnOff")));
            string temp = await WriteAsync("taken.cfg.damage.tmp", Bytes(false, Config("\n", "Destroy", "Destroy")));
            Directory.CreateDirectory(temp + ".kept");

            (List<string> changed, string? recovered) = await ReplaceDamageWithRecoveryAsync(path);
            Assert.Empty(changed);

            string kept = Assert.Single(Directory.GetFiles(_folder, "taken.cfg.damage.tmp.*.kept"));
            Assert.Equal(Bytes(false, Config("\n", "Destroy", "Destroy")), await ReadAsync(kept));
            Assert.False(File.Exists(temp));
            Assert.Contains(kept, recovered, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ACopyTheSameAsTheConfigIsRemoved()
        {
            string path = await WriteAsync("same.cfg", Bytes(false, Config("\n", "TurnOff", "Destroy")));
            string temp = await WriteAsync("same.cfg.damage.tmp", Bytes(false, Config("\n", "TurnOff", "Destroy")));

            (List<string> changed, string? recovered) = await ReplaceDamageWithRecoveryAsync(path);
            Assert.Empty(changed);

            Assert.False(File.Exists(temp));
            Assert.Null(recovered);
        }

        [Fact]
        public void ANumberOneIsNamedAsSuch()
        {
            string line = InstantPunishmentFile.LogLine(new[] { InstantPunishmentFile.StoredAsOne("InstantKillPunishment") });

            Assert.Contains("InstantKillPunishment (stored as the number 1, which was Damage before 1.3.2 and Destroy in 1.3.2)", line, StringComparison.Ordinal);
        }

        [Fact]
        public void TheLogLineNamesTheSettings()
        {
            string line = InstantPunishmentFile.LogLine(MemoryOnly);

            Assert.StartsWith("The config held Damage for MemoryPunishment,", line, StringComparison.Ordinal);
        }
    }
}
