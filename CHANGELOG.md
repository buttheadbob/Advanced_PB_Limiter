# Changelog

## 1.3.2
- Instant kills, by run time and by memory, now offer only Turn Off and Destroy. With Damage the script kept running, so every run over the limit dealt damage again, logged an error and messaged the owner, up to 60 times a second for a script on Update1; in a safe zone with damage off it never stopped. Ordinary offences keep Damage and their warnings as before.
- A config, a Quasar-managed config or a Quasar profile that set Damage for either is read as Turn Off, the default, and the log says so once. Set Destroy again if that was meant.
- Magnetar servers updating from 1.3.0, where instant kills and the memory limit always destroyed the block, run with Turn Off for these two settings: Magnetar's config only holds a value that was chosen, and 1.3.0 had none. Admins who relied on Destroy should set it in Quasar. A Destroy chosen in 1.3.1 is kept.
