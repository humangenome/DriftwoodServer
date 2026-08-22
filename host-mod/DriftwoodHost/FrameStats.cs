using System;

namespace DriftwoodHost
{
	// The server's real frame rate and frame-time spread.
	//
	// Two jobs. First, it answers the question a frame cap always raises: IS THE CAP BINDING? A
	// server that cannot reach its cap is not being limited by it, and every "the cap saved us N%"
	// claim about such a box is wrong. Playbook 2b's warning is the mirror image of this - the
	// engine silently discards a frame-rate target while vSync is on, so anyone who changes one
	// line measures no improvement and concludes the whole finding was wrong.
	//
	// Second, it is the smoothness metric (2c). The world simulation runs off the same loop that
	// would draw frames, so a stalled frame is a stalled world for every connected player - a
	// garbage-collection pause of 100-180 ms once a second is what "the server feels laggy" is
	// actually made of, and it is invisible to average frame rate.
	internal sealed class FrameStats
	{
		private const int Capacity = 512;
		private readonly float[] _frameMilliseconds = new float[Capacity];
		private int _count;
		private int _next;
		private readonly object _sync = new object();

		public void Sample(float deltaSeconds)
		{
			if (deltaSeconds <= 0f) return;
			lock (_sync)
			{
				_frameMilliseconds[_next] = deltaSeconds * 1000f;
				_next = (_next + 1) % Capacity;
				if (_count < Capacity) _count++;
			}
		}

		public void Snapshot(out double framesPerSecond, out double meanMs, out double p95Ms, out double worstMs)
		{
			float[] copy;
			int count;
			lock (_sync)
			{
				count = _count;
				copy = new float[count];
				Array.Copy(_frameMilliseconds, copy, count);
			}
			if (count == 0)
			{
				framesPerSecond = 0;
				meanMs = p95Ms = worstMs = 0;
				return;
			}
			Array.Sort(copy);
			double total = 0;
			for (int i = 0; i < count; i++) total += copy[i];
			meanMs = total / count;
			p95Ms = copy[Math.Min(count - 1, (int)(count * 0.95))];
			worstMs = copy[count - 1];
			framesPerSecond = meanMs > 0 ? 1000.0 / meanMs : 0;
		}
	}
}
