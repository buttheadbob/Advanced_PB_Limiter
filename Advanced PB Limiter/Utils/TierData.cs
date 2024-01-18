namespace Advanced_PB_Limiter.Utils
{
    public class TierData
    {
        public string Name;
        public string Description;
        public int Update1;
        public int Update10;
        public int Update100;

        /// <summary>
        /// Do not use this constructor.  This is for serializers.
        /// </summary>
        public TierData() { }

        public TierData(string name, string description, int update1, int update10, int update100)
        {
            Name = name;
            Description = description;
        }
    }
}