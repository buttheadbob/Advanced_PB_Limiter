#nullable enable
using System;

namespace Advanced_PB_Limiter.Utils
{
    public static class Enums
    {
        // The numbers are stored in configs and read back by TryFromStoredNumber, so none is ever reused or reordered.
        public enum Punishment
        {
            TurnOff = 0,
            Damage = 1,
            Destroy = 2
        }

        /// <summary>
        /// What an instant kill does, by run time or by memory. It punishes after one sample with no warning, so it has no
        /// Damage: a damaged block keeps running, and would be punished again on every run.
        /// </summary>
        public enum InstantPunishment
        {
            TurnOff = 0,
            Destroy = 1
        }

        public static Punishment ToPunishment(this InstantPunishment punishment)
        {
            return punishment == InstantPunishment.Destroy ? Punishment.Destroy : Punishment.TurnOff;
        }

        /// <summary>
        /// Reads an instant punishment stored as a number. Before 1.3.2 these settings were a <see cref="Punishment"/>, so
        /// the number is read by its values: 0 TurnOff, 1 Damage (read as TurnOff, <paramref name="wasDamage"/>), 2 Destroy.
        /// No release wrote these settings as numbers, so a number is a hand edit. 1.3.2 read 1 as Destroy, so 1 is read
        /// as the milder TurnOff rather than destroy blocks on a guess, and the caller names it with
        /// InstantPunishmentFile.StoredAsOne.
        /// </summary>
        public static bool TryFromStoredNumber(long number, out InstantPunishment punishment, out bool wasDamage)
        {
            punishment = number == (long)Punishment.Destroy ? InstantPunishment.Destroy : InstantPunishment.TurnOff;
            wasDamage = number == (long)Punishment.Damage;
            switch (number)
            {
                case (long)Punishment.TurnOff:
                case (long)Punishment.Damage:
                case (long)Punishment.Destroy:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Reads an instant punishment stored by name, in any case as the serializers allow. Damage, offered before 1.3.2,
        /// is read as TurnOff (<paramref name="wasDamage"/>). Spaces around it are ignored. Only whole names count: a number
        /// written as text is refused.
        /// </summary>
        public static bool TryFromStoredName(string text, out InstantPunishment punishment, out bool wasDamage)
        {
            text = text.Trim();
            punishment = InstantPunishment.TurnOff;
            wasDamage = string.Equals(text, nameof(Punishment.Damage), StringComparison.OrdinalIgnoreCase);
            if (wasDamage) return true;

            foreach (InstantPunishment value in (InstantPunishment[])Enum.GetValues(typeof(InstantPunishment)))
            {
                if (!string.Equals(text, value.ToString(), StringComparison.OrdinalIgnoreCase)) continue;
                punishment = value;
                return true;
            }
            return false;
        }
    }
}
