# Advanced PB Limiter

## This is a full replacement for the current PB Limiter by HaE.

**Current release: 1.3.4.5.** See [CHANGELOG.md](CHANGELOG.md) for the changes in this and earlier releases.

### Requirements
- **Torch:** a Torch server.
- **Magnetar:** 2.4.2.1 or later. 1.3.3 reads PluginSdk's `ManagedPluginConfiguration`, first available in 2.4.2.1.
- **Quasar UI plugin:** Quasar 1.7 or later.

### Features
- Track run time per programmable block and across all of a player's blocks, using sim-speed time or wall time.
- Limit memory use per block. The check handles Linq, MyIni and deep or hostile scripts; a block it cannot measure is reported as "not measurable" and does not stop the check for other blocks.
- Instant-kill a block over an extreme run-time or memory limit. Instant kills use Turn Off or Destroy. Destroy stops at 100 integrity, or removes the block when the game will not damage it. A config that set Damage for either is read as Turn Off.
- Ordinary offences can use Turn Off, Damage or Destroy. Damage on a block the game will not damage turns it off, or removes it when "destroy protected PB" is on. A damage amount of 0 turns the block off.
- Privileged players with their own higher or lower limits, per block and combined.
- Prevent a PB from re-enabling itself with the Save()/constructor exploit, with separate settings for players and NPCs.
- Warn scripts before punishment and send a graceful shutdown with the seconds remaining; the time is configurable and can differ for privileged players.
- Send a last-offence warning with a code for the offence, so scripts can cool down or self-throttle.
- Apply punishments always, or only when sim speed is below a set value.
- Defer runtime checks never, always, or when sim speed falls below a set minimum.
- Disable Update1: PBs run Update1 with Update10 instead of every tick, without requiring script or player changes.
- Require a recompile on restart, to block restart exploits.
- Use Tebex or another pay model to give players individual limits through in-game commands.
- Keep resting or slow timer scripts' runs, offences and startup time; they are marked resting and left out of combined reports.
- Combined limits and reports count the same blocks, by the same rules. Reports use the enabled/working state cached by the last tracking cleanup, so their totals can trail the limits by up to about 5 seconds; the limits read the live state. Blocks that cannot run are left out unless they have run since the last combined check.
- Count runs that trigger a garbage collection against the run-time, average and combined limits. Self-turn-off scripts, TryRun, owner changes and recompiles are handled.
- Save config atomically and recover it. A failed save retries every 30 seconds; a crash leaves the old or new file, never half a file; a cut-short file is restored at the next start; an unreadable config is moved aside; a config held by another program is left alone.
- Stay inactive instead of stopping Torch when a game update breaks a patched method. Commands and tabs say so. A plugin reload leaves the limiter off until Torch restarts.
- Write numbers with a decimal point on every server culture. Sanitize block, grid and player names in logs, chat and reports; long names are cut to 100 characters.
- Report from the Torch UI's live view and Reports tab, from the Quasar UI, or from in-game commands. Reports list up to 10, 20 or 200 blocks in detail; totals count every block.

### Graceful Shutdowns
This sends a run argument to the programming block:

`GracefulShutDown::seconds`

`seconds` is the time the script has to shut down before punishment. The punishment is applied after that time regardless. A negative value is a warning with no time limit: `-1` the block reached its allowed offences for run time, `-2` the same for average run time, and `-3` the player's blocks are over a combined limit.

Scripters can add a simple check:

```csharp
if (updateSource == UpdateType.Script)
{
    // Parse the command and its arguments
    if (argument.StartsWith("GracefulShutDown::"))
    {
        var args = argument.Split(new[] { "::" }, StringSplitOptions.None);
        if (args.Length > 1)
        {
            int gracePeriodSeconds;
            if (int.TryParse(args[1], out gracePeriodSeconds))
            {
                // Now you have the gracePeriodSeconds parsed
                // You can implement your graceful shutdown logic here
            }
            else
            {
                // Handle the case where the seconds part is not a valid integer
            }
        }
    }
}
```

### Commands
Everything is under `!PBLimiter`, on both Torch and Magnetar.

Players: `About`, `Limits`, `MyStats`, `Runtimes`.

Admins: `GetReport`, `Enable`, `Disable`, `debug <true|false>`, `requestpbshutdown [seconds]`, `CreatePrivilegedUser`, `AdminHelp`, `DetailedAdminHelp`.

Owner: `AllowStaffCommands`, `DenyStaffCommands`.

On Magnetar, `!PBLimiter help` is generated for you and `!PBLimiter Timings` logs the limiter's own timings for 5 seconds. On Torch that is the Timings Burp button.

### Torch and Magnetar
The plugin runs on both, and both builds share the same code in `Shared`.

- `TorchPlugin` The Torch plugin, configured on its tab in Torch. Its Reports tab is a live dashboard with summary tiles, a resting-block filter, and per-block and per-player tables; saved reports always include every tracked block, including resting ones.
- `ServerPlugin` The Magnetar plugin, configured in Quasar under Server plugins. Its options and commands match Torch. It builds for .NET Framework 4.8 and .NET 10, and needs Magnetar 2.4.2.1 or later, the first with PluginSdk's `ManagedPluginConfiguration`, which it reads to tell a node of a managed cluster.
- `QuasarPlugin` The optional Quasar plugin, for the servers Quasar manages. It needs Quasar 1.7 or later: the pages are a browser assembly for the WebAssembly dashboard, and a worker adapter answers its authorized SDK calls. Its companion talks to the Magnetar plugin.
  - **Live view:** a client-side dashboard with server status, summary cards, a session trend, search and state filters, paged block and player tables, a details dialog, CSV export, and Turn off for blocks and players. It remembers the chosen server, refresh rate, sort, filter and page size in the browser.
  - **Profiles:** a saved-profile list, a save card, an apply flow with a review step, a waiting-to-apply timeline, and offline servers. Set up one server on its Plugins page, save it as a named profile, and apply it to your other servers. Applying replaces every limiter setting and the privileged players. A server that is offline or still loading gets the profile when it is ready, and queued profiles survive a Quasar restart. A profile is applied by hand: the server isn't linked to it afterwards, so a later change to the profile reaches a server only when it is applied again. Needs the `CanEditConfigs` right.
  - **Generate report:** choose servers from cards, watch each one answer, then preview, copy, download or export the report as CSV. One text file covers the servers you choose, with totals across all of them and the detail of each.

The Torch and Magnetar plugins keep separate config files, so settings do not carry over from one to the other. Beside each config, and beside Quasar's profiles file, a small `.lock` file stays: it keeps two writers of the file apart and can be left alone. Where no lock file can be made there, on a read-only folder say, the log says so once.

### Building
Run `Setup.ps1` once after cloning, then `build.ps1`, from PowerShell. Windows blocks scripts by default; run them as `powershell -ExecutionPolicy Bypass -File .\build.ps1`, or allow local scripts once with `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`. On Linux or macOS, build Magnetar and the Quasar plugin with PowerShell 7, for example `pwsh ./build.ps1 magnetar`; Torch builds on Windows only. `Setup.ps1` writes the gitignored `Directory.Build.props.user` with the local game and Torch paths, and a Magnetar path when it finds one, keeping any Magnetar or Quasar overrides already there. `Directory.Build.props.user.example` shows every setting and its default. No junctions or other links are used. If an older checkout still has `GameBinaries` or `TorchBinaries`, remove the links themselves, not the folders they point to (`rmdir GameBinaries` in Command Prompt removes a junction without touching its target). A checkout made before the user-props split has a local `Directory.Build.props`: copy its values to `Directory.Build.props.user` (or run `Setup.ps1`), remove the old local file, then update.

- `build.ps1` Builds all of them for release. Add `torch`, `magnetar` or `quasar` to build one, for example `.\build.ps1 magnetar`.
- `dotnet test Tests` Runs the shared tests on .NET Framework 4.8 and .NET 10 on Windows; on Linux it runs the .NET 10 tests.

The Torch build needs Visual Studio and creates `Build\Advanced PB Limiter.zip`. It reads `GameBinaries` (or `DS64`/`Dedicated64`) and `TorchBinaries` from `Directory.Build.props.user`; junctions or other links are not used. The Magnetar build needs the .NET SDK and a Magnetar install, or the path to `PluginSdk.dll` set as `MagnetarBin` in `Directory.Build.props.user`. The Quasar plugin needs Quasar 1.7: a checkout in `..\quasar`, or `QuasarPluginAbstractionsAssembly` in `Directory.Build.props.user` (or `build.ps1 -QuasarAbstractions`) pointing at its `Quasar.Plugin.Abstractions.dll`.

Each `build.ps1` run raises the last part of the version in `Versioning\AssemblyVersion.cs` and copies it into `TorchPlugin\manifest.xml` and `quasar-plugin.json`. `AdvancedPBLimiter.xml` is the MagnetarHub manifest and `AdvancedPBLimiter.Quasar.xml` the QuasarHub one: set their `Commit` when publishing.

### License
Advanced PB Limiter is licensed under the [GNU Affero General Public License](LICENSE), version 3 or (at your option) any later version, from version 1.3.1. Earlier versions were released under the MIT License.

It replaces [HaE PBLimiter](https://github.com/sirhamsteralot/HaE-PBLimiter) by sirhamsteralot, and the run timing at its core comes from that plugin. Those parts remain under the MIT License, with sirhamsteralot's notice. [NOTICE](NOTICE) lists them.
