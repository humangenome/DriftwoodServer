using System;
using System.Collections.Generic;
using System.Globalization;

namespace DriftwoodHost
{
	// Who may hold a connection to the API port, and how much of a request body is read
	// before anybody has proved who they are. No Unity, no BepInEx, no sockets: the xunit
	// suite links this file and proves every rule here without a running game.
	//
	// THE ORDER THAT MATTERS: headers first, body second. A caller that has not signed is
	// never handed more than a few kilobytes of read, and never a temp file. A signed upload
	// carries the body's sha256 in a header, so its signature is checked over the headers
	// alone, before the first body byte is accepted, and the streamed body must then hash to
	// exactly that value.
	internal enum GateTier
	{
		Public,
		LoopbackOrSigned,
		Signed
	}

	internal enum BodyPlan
	{
		// No body. A signed route verifies over the empty-body hash.
		NoBody,
		// Read the (small) body; no signature is needed on this route for this caller.
		Read,
		// Read a small body, then verify the signature over its hash (callers that do not
		// send the content-hash header: the panel, the supervisor, the web tools).
		ReadThenVerify,
		// Verify the signature over the declared content hash first, then read or stream
		// the body and require it to hash to the declared value.
		VerifyThenRead,
		RefuseUnauthorised,
		RefuseTooLarge,
		// A remote body this size must declare its hash up front (the Driftwood app does
		// from 0.1.5). Refused before any of it is read.
		RefuseNeedsHash
	}

	internal static class ApiGate
	{
		// The identity claim is the only public route with a body, and it is three short strings.
		internal const int PublicBodyBytes = 4 * 1024;
		// Largest body a remote caller may send without declaring its hash. Every JSON call the
		// panel, the web tools and the app make is far below this.
		internal const int UnhashedBodyBytes = 16 * 1024;
		internal const int MaxJsonBodyBytes = 64 * 1024;

		// Whole-request-head deadline, and the deadline for a small body after it. A slow
		// caller holds a slot for at most this long.
		internal const int HeadDeadlineMs = 10000;
		internal const int SmallBodyDeadlineMs = 10000;

		// Connection pools. Loopback (the panel's poll and its save before a stop, the
		// supervisor) has its own pool that remote callers can never fill. Remote callers share
		// one pool with a per-address cap. A signed, verified transfer (an upload or a snapshot
		// download) moves to its own small pool, so it does not hold a remote slot either.
		internal const int LoopbackCap = 8;
		internal const int RemoteCap = 16;
		internal const int PerAddressCap = 4;
		internal const int LongCap = 4;

		internal static bool NeedsSignature(GateTier tier, bool loopback)
		{
			return tier == GateTier.Signed || (tier == GateTier.LoopbackOrSigned && !loopback);
		}

		internal static long BodyCap(GateTier tier, bool upload, long maxUploadBytes)
		{
			if (upload) return maxUploadBytes;
			return tier == GateTier.Public ? PublicBodyBytes : MaxJsonBodyBytes;
		}

		internal static bool IsSha256Hex(string value)
		{
			if (value == null || value.Length != 64) return false;
			for (int i = 0; i < value.Length; i++)
			{
				char c = value[i];
				bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
				if (!hex) return false;
			}
			return true;
		}

		// The cheap part of the signature check, from headers alone: a timestamp inside the
		// window and something shaped like an HMAC-SHA256 hex digest. Says nothing about
		// whether the caller holds the secret; it only decides whether a body is worth reading.
		internal static bool HeaderShapeOk(string timestampHeader, string signatureHeader, long nowUnix, int windowSeconds)
		{
			if (string.IsNullOrEmpty(timestampHeader) || string.IsNullOrEmpty(signatureHeader)) return false;
			long timestamp;
			if (!long.TryParse(timestampHeader.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out timestamp)) return false;
			if (Math.Abs(nowUnix - timestamp) > windowSeconds) return false;
			return IsSha256Hex(signatureHeader.Trim());
		}

		internal static BodyPlan Plan(GateTier tier, bool loopback, bool upload, long contentLength,
			string contentShaHeader, bool authUsable, bool headerShapeOk, long maxUploadBytes)
		{
			long length = contentLength < 0 ? 0 : contentLength;
			if (length > BodyCap(tier, upload, maxUploadBytes)) return BodyPlan.RefuseTooLarge;

			if (!NeedsSignature(tier, loopback))
			{
				return length > 0 ? BodyPlan.Read : BodyPlan.NoBody;
			}

			if (!authUsable || !headerShapeOk) return BodyPlan.RefuseUnauthorised;
			if (length == 0) return BodyPlan.NoBody;

			if (contentShaHeader != null)
			{
				return IsSha256Hex(contentShaHeader.Trim()) ? BodyPlan.VerifyThenRead : BodyPlan.RefuseUnauthorised;
			}

			// No declared hash. On this box (the panel, the supervisor) that is today's
			// behaviour: a local caller can already do anything a signature allows. From the
			// network only a small body is read before the signature is checked.
			if (loopback) return BodyPlan.ReadThenVerify;
			if (upload || length > UnhashedBodyBytes) return BodyPlan.RefuseNeedsHash;
			return BodyPlan.ReadThenVerify;
		}
	}

	internal sealed class GateTicket
	{
		internal string Address;
		internal bool Loopback;
		internal bool Long;
		internal bool Released;
	}

	internal sealed class ConnectionGate
	{
		private readonly object _sync = new object();
		private readonly Dictionary<string, int> _perAddress = new Dictionary<string, int>(StringComparer.Ordinal);
		private readonly int _loopbackCap;
		private readonly int _remoteCap;
		private readonly int _perAddressCap;
		private readonly int _longCap;
		private int _loopback;
		private int _remote;
		private int _long;

		internal ConnectionGate(int loopbackCap, int remoteCap, int perAddressCap, int longCap)
		{
			_loopbackCap = loopbackCap;
			_remoteCap = remoteCap;
			_perAddressCap = perAddressCap;
			_longCap = longCap;
		}

		internal int LoopbackInUse { get { lock (_sync) return _loopback; } }
		internal int RemoteInUse { get { lock (_sync) return _remote; } }
		internal int LongInUse { get { lock (_sync) return _long; } }
		internal int ForAddress(string address)
		{
			lock (_sync)
			{
				int n;
				return _perAddress.TryGetValue(address ?? string.Empty, out n) ? n : 0;
			}
		}

		// null = refused: close the connection without reading anything.
		internal GateTicket TryAdmit(string address, bool loopback)
		{
			address = address ?? string.Empty;
			lock (_sync)
			{
				if (loopback)
				{
					if (_loopback >= _loopbackCap) return null;
					_loopback++;
					return new GateTicket { Address = address, Loopback = true };
				}
				if (_remote >= _remoteCap) return null;
				int mine;
				_perAddress.TryGetValue(address, out mine);
				if (mine >= _perAddressCap) return null;
				_remote++;
				_perAddress[address] = mine + 1;
				return new GateTicket { Address = address, Loopback = false };
			}
		}

		// A verified long transfer leaves the remote pool for the long pool. False = the long
		// pool is full (the caller answers 503 and the ticket stays where it was).
		internal bool TryPromoteLong(GateTicket ticket)
		{
			if (ticket == null || ticket.Released) return false;
			if (ticket.Loopback || ticket.Long) return true;
			lock (_sync)
			{
				if (_long >= _longCap) return false;
				_long++;
				_remote--;
				ticket.Long = true;
				return true;
			}
		}

		internal void Release(GateTicket ticket)
		{
			if (ticket == null) return;
			lock (_sync)
			{
				if (ticket.Released) return;
				ticket.Released = true;
				if (ticket.Loopback)
				{
					_loopback--;
					return;
				}
				if (ticket.Long) _long--; else _remote--;
				int mine;
				if (_perAddress.TryGetValue(ticket.Address, out mine))
				{
					if (mine <= 1) _perAddress.Remove(ticket.Address); else _perAddress[ticket.Address] = mine - 1;
				}
			}
		}
	}
}
