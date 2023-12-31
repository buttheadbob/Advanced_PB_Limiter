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

### TODO:
- Add support for Nexus, allowing controller plugins or other plugins/mods to issue commands or add/remove/manage privileged users.
- UI still needs work.
- Commands
- Reporting methods
- Possibly Discord Webhook integrations for reporting or other stuff...
