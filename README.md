# Advanced PB Limiter  

## This is a full replacement for the current PB Limiter by HaE.  

### Features  
- Realtime time tracking only.
- Privileged Users with higher or lower limits.
- Limits per programming block and limits for all programming blocks owned by a user as a group.
- Overlimit groups can be shutdown entirely or a random programming block shutdown.
- Punishments include ( Damage, Destroy, and Turn Off ).
- Graceful Shutdowns

  ### Graceful Shutdowns
  This will send a run argument to the programming block.
  The argument is `GracefulShutDown::seconds` with seconds being the amount of time the script has to shutdown before any punishment is incurred.
  The punishment will always be applied after the time is up, regardless!
  A negative value is a warning, with no time limit: `-1` the block has reached its allowed offences for run time, `-2` the same for average run time, and `-3` the player's blocks are over a combined limit.
  Scripters can add a simple check with the following example:
```
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
- `TorchPlugin` The Torch plugin, configured on its tab in Torch, with the live view on its Reports tab.
- `ServerPlugin` The Magnetar plugin, configured in Quasar under Server plugins. Its options and commands match Torch. It builds for .NET Framework 4.8 and .NET 10.
- `QuasarPlugin` The optional Quasar plugin, for the servers Quasar manages. Its companion talks to the Magnetar plugin.
  - **Live view:** what the Reports tab shows, with Turn off for blocks and players.
  - **Profiles:** set up one server on its Plugins page, save it as a named profile, and apply it to your other servers. Applying replaces every limiter setting and the privileged players. A server that is offline or still loading gets the profile when it is ready, and queued profiles survive a Quasar restart. Needs the `CanEditConfigs` right.
  - **Generate report:** one text file for the servers you choose, with totals across all of them and the detail of each.

The Torch and Magnetar plugins keep separate config files, so settings do not carry over from one to the other.

### Building
Run `Setup (run before opening solution).bat` once after cloning, then `build.bat`.
- `build.bat` Builds all of them for release. Add `torch`, `magnetar` or `quasar` to build one, for example `build.bat magnetar`.

The Torch build needs Visual Studio and creates `Build\Advanced PB Limiter.zip`. The Magnetar build needs the .NET SDK and a Magnetar install, or the path to `PluginSdk.dll` set in `Directory.Build.props`. The Quasar plugin needs a checkout of Quasar in `..\quasar`.

Each `build.bat` run raises the last part of the version in `Versioning\AssemblyVersion.cs` and copies it into `TorchPlugin\manifest.xml` and `quasar-plugin.json`. `AdvancedPBLimiter.xml` is the MagnetarHub manifest and `AdvancedPBLimiter.Quasar.xml` the QuasarHub one: set their `Commit` when publishing.

### License
[MIT](LICENSE). The profiling at the core of this plugin comes from [HaE PBLimiter](https://github.com/sirhamsteralot/HaE-PBLimiter), also MIT, and its notice is kept in the license file.

### TODO:
- UI still needs work.
- Commands
- Reporting methods
- Possibly Discord Webhook integrations for reporting or other stuff...
