namespace AutoPilot.WebAPI.Services
{
	// Thread-safe accumulator of the Copilot cost (in nano-AIU) of a single run; usage events may arrive concurrently
	internal sealed class RunCost
	{
		private long nanoAiu;

		public void Add(double nano) => Interlocked.Add(ref nanoAiu, (long)Math.Round(nano));

		public decimal Aiu => Interlocked.Read(ref nanoAiu) / 1_000_000_000m;
	}
}
