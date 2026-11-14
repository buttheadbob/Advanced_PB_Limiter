namespace Advanced_PB_Limiter.Utils
{
    public static class Enums
    {
        public enum Punishment
        {
            TurnOff,
            Damage,
            Destroy
        }

        /// <summary>
        /// What an instant kill does, by run time or by memory. It punishes after one sample with no warning, so it has no
        /// Damage: a damaged block keeps running, and would be punished again on every run.
        /// </summary>
        public enum InstantPunishment
        {
            TurnOff,
            Destroy
        }

        public static Punishment ToPunishment(this InstantPunishment punishment)
        {
            return punishment == InstantPunishment.Destroy ? Punishment.Destroy : Punishment.TurnOff;
        }
    }
}
