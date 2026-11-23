namespace Advanced_PB_Limiter.Utils
{
    /// <summary>
    /// Remembers the batch of queued runs in which something happened to a block, an instant kill say, so a later run of
    /// the same block in that batch (it ran more than once in a tick) does not do it again. Decided by the batch alone, not
    /// by whether the block works when the batch is processed: a script that turns its own block off at the end of each run
    /// is still judged on every run. Game thread only.
    /// </summary>
    public sealed class BatchMark
    {
        private long _batch = -1;

        /// <summary>True when the block was marked in <paramref name="batch"/>.</summary>
        public bool In(long batch) => _batch == batch;

        public void Set(long batch) => _batch = batch;
    }
}
