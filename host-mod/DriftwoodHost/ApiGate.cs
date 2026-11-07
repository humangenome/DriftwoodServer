using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;

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
		RefuseNeedsHash,
		// This address sent too many wrong signatures and is turned away for a while
		// (AuthFailureGuard). Refused before any of the body is read.
		RefuseBlocked
	}

	// What a signature check found, for the wrong-signature limit.
	internal enum SignatureVerdict
	{
		Valid,
		// Well formed, timestamp fresh, and it does not match: a guess at the key. Counted.
		Wrong,
		// Missing or malformed headers, a stale timestamp (a clock problem, not a guess) or
		// a server with no key. Refused, never counted.
		NotCounted
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
			return Plan(tier, loopback, upload, contentLength, contentShaHeader, authUsable, headerShapeOk,
				maxUploadBytes, 0);
		}

		// blockedSeconds: how long this caller's address is still turned away for wrong
		// signatures (AuthFailureGuard.BlockedSeconds; always 0 for loopback). A turned-away
		// address is refused on every route that needs a signature before anything else is
		// decided, so not one body byte is read; the public routes stay open to it.
		internal static BodyPlan Plan(GateTier tier, bool loopback, bool upload, long contentLength,
			string contentShaHeader, bool authUsable, bool headerShapeOk, long maxUploadBytes, long blockedSeconds)
		{
			if (blockedSeconds > 0 && NeedsSignature(tier, loopback)) return BodyPlan.RefuseBlocked;

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

		// The signature check itself, minus the replay guard (which needs the server's memory
		// of signatures already used). bodySha is the hash the signature must cover. Only a
		// well-formed signature with a fresh timestamp that does not match is Wrong, so only a
		// real guess at the key counts toward the address's block.
		internal static SignatureVerdict CheckSignature(byte[] key, bool authUsable, string method, string path,
			string timestampHeader, string signatureHeader, string bodySha, long nowUnix, int windowSeconds)
		{
			if (!authUsable || key == null) return SignatureVerdict.NotCounted;
			if (!HeaderShapeOk(timestampHeader, signatureHeader, nowUnix, windowSeconds)) return SignatureVerdict.NotCounted;

			long timestamp = long.Parse(timestampHeader.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
			string canonical = ApiSignature.Canonical(method, path, timestamp, bodySha ?? string.Empty);
			string expected = ApiSignature.Compute(key, canonical);
			string supplied = signatureHeader.Trim().ToLowerInvariant();
			return ApiSignature.ConstantTimeEquals(expected, supplied) ? SignatureVerdict.Valid : SignatureVerdict.Wrong;
		}
	}

	// The wrong-signature limit, per caller address (the socket's peer address, never a
	// header). The signed routes download and replace the world with a key derived from the
	// owner's password, so guessing that password must not be free: five wrong signatures in
	// ten minutes turn the address away for a minute, then two, four, up to fifteen. A good
	// signature clears the count. Loopback (the box's own tools) is never limited. Pure
	// bookkeeping, no IO, thread-safe: the API decides what to do with the answers.
	internal sealed class AuthFailureGuard
	{
		internal const int FailuresBeforeBlock = 5;
		internal const long FailureWindowMs = 10L * 60 * 1000;
		internal const long FirstBlockMs = 60L * 1000;
		internal const long MaxBlockMs = 15L * 60 * 1000;
		// The failure table makes room at MaxTrackedAddresses (see MakeRoom). It grows past that
		// only while every address in it is blocked, and never past HardCeiling.
		private const int MaxTrackedAddresses = 4096;
		private const int HardCeiling = 65536;
		private const int RoomBatch = MaxTrackedAddresses / 16;

		private sealed class FailState
		{
			internal int Failures;
			internal long WindowStartMs;
			internal long BlockedUntilMs;
			internal int Strikes;
			internal long LastFailureMs;
		}

		private readonly object _sync = new object();
		private readonly Dictionary<string, FailState> _fails = new Dictionary<string, FailState>(StringComparer.Ordinal);
		private int _lookAgainAt = MaxTrackedAddresses;
		private long _lookAgainMs = long.MaxValue;
		private bool _allBlockedNoted;
		private string _notice;

		internal static bool IsLoopback(string address)
		{
			IPAddress parsed;
			return IPAddress.TryParse(address ?? string.Empty, out parsed) && IPAddress.IsLoopback(parsed);
		}

		// Seconds left on this address's block; 0 when it may talk.
		internal long BlockedSeconds(string address, long nowMs)
		{
			if (string.IsNullOrEmpty(address) || IsLoopback(address)) return 0;
			lock (_sync)
			{
				FailState state;
				if (!_fails.TryGetValue(address, out state)) return 0;
				long left = state.BlockedUntilMs - nowMs;
				return left > 0 ? (left + 999) / 1000 : 0;
			}
		}

		// Records one wrong signature; true when this one starts a block (log it then, once).
		internal bool Failure(string address, long nowMs)
		{
			if (string.IsNullOrEmpty(address) || IsLoopback(address)) return false;
			lock (_sync)
			{
				FailState state;
				if (!_fails.TryGetValue(address, out state))
				{
					if (_fails.Count >= MaxTrackedAddresses && (_fails.Count >= _lookAgainAt || nowMs >= _lookAgainMs))
						MakeRoom(nowMs);
					state = new FailState { WindowStartMs = nowMs };
					_fails[address] = state;
				}
				state.LastFailureMs = nowMs;
				if (nowMs - state.WindowStartMs > FailureWindowMs)
				{
					state.Failures = 0;
					state.WindowStartMs = nowMs;
				}
				state.Failures++;
				if (state.Failures < FailuresBeforeBlock) return false;

				long block = FirstBlockMs;
				for (int i = 0; i < state.Strikes && block < MaxBlockMs; i++) block *= 2;
				if (block > MaxBlockMs) block = MaxBlockMs;
				state.BlockedUntilMs = nowMs + block;
				state.Strikes++;
				state.Failures = 0;
				state.WindowStartMs = nowMs;
				return true;
			}
		}

		internal void Success(string address)
		{
			if (string.IsNullOrEmpty(address)) return;
			lock (_sync) _fails.Remove(address);
		}

		// The one line to log when the failure table first fills with blocks, with no address in
		// it; null otherwise. The API calls this after Failure and logs what it gets.
		internal string TakeNotice()
		{
			lock (_sync)
			{
				string notice = _notice;
				_notice = null;
				return notice;
			}
		}

		// The one line logged when an address is turned away: the address, the door and how
		// long. Once per block, never per wrong try, and never the token or a signature.
		internal string BlockLine(string address, long nowMs)
		{
			return "auth block: door=http address=" + address + " blocked=" +
				BlockedSeconds(address, nowMs).ToString(CultureInfo.InvariantCulture) + "s after " +
				FailuresBeforeBlock.ToString(CultureInfo.InvariantCulture) + " wrong tries in " +
				(FailureWindowMs / 60000).ToString(CultureInfo.InvariantCulture) + " min";
		}

		// Makes room in a full failure table for a new address. First go the addresses that are
		// neither blocked nor inside a live failure window, then the longest-quiet addresses that
		// are not blocked, a batch at a time. An active block is never dropped to make room: when
		// every address left is blocked, the table keeps them all and grows (one log line, no
		// addresses), up to HardCeiling. Only at the ceiling does the block nearest to running out
		// make way. Runs once per batch of new addresses or when the next block runs out, not on
		// every failure.
		private void MakeRoom(long nowMs)
		{
			List<string> quiet = new List<string>();
			List<KeyValuePair<string, long>> open = new List<KeyValuePair<string, long>>();
			string soonest = null;
			long soonestUntil = long.MaxValue;
			foreach (KeyValuePair<string, FailState> pair in _fails)
			{
				FailState state = pair.Value;
				if (state.BlockedUntilMs > nowMs)
				{
					if (state.BlockedUntilMs < soonestUntil)
					{
						soonestUntil = state.BlockedUntilMs;
						soonest = pair.Key;
					}
				}
				else if (nowMs - state.WindowStartMs > FailureWindowMs) quiet.Add(pair.Key);
				else open.Add(new KeyValuePair<string, long>(pair.Key, state.LastFailureMs));
			}
			foreach (string key in quiet) _fails.Remove(key);

			int excess = _fails.Count - (MaxTrackedAddresses - RoomBatch);
			if (excess > 0 && open.Count > 0)
			{
				open.Sort((a, b) => a.Value.CompareTo(b.Value));
				for (int i = 0; i < excess && i < open.Count; i++) _fails.Remove(open[i].Key);
			}

			if (_fails.Count < MaxTrackedAddresses)
			{
				_lookAgainAt = MaxTrackedAddresses;
				_lookAgainMs = long.MaxValue;
				_allBlockedNoted = false;
				return;
			}

			// Every address left is an active block.
			if (!_allBlockedNoted)
			{
				_allBlockedNoted = true;
				_notice = "auth table full: all " + _fails.Count.ToString(CultureInfo.InvariantCulture) +
					" tracked addresses are blocked; every block is kept (up to " +
					HardCeiling.ToString(CultureInfo.InvariantCulture) + ")";
			}
			if (_fails.Count >= HardCeiling && soonest != null)
			{
				_fails.Remove(soonest);
				_lookAgainAt = HardCeiling;
			}
			else
			{
				_lookAgainAt = Math.Min(HardCeiling, _fails.Count + RoomBatch);
			}
			_lookAgainMs = soonestUntil;
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
