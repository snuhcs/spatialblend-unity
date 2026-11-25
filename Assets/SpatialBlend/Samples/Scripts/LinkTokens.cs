// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace SpatialBlend.Samples
{
    /// <summary>
    /// Who sent a piece of the core link, without trusting anything the sender writes. Fusion's reliable-data callback
    /// does not say who sent (it reports the receiving player, measured in single-process and two-process runs), so the
    /// link authenticates itself with a capability: before a peer may send to me on round E, I give it a 128-bit
    /// random token over a channel whose sender Photon stamps (a targeted Fusion RPC; <c>RpcInfo.Source</c> was measured
    /// correct on both sides), addressed to that peer alone. Every chunk and signal it sends me carries that token, and I
    /// resolve the token to the peer I issued it to. A token I never issued, one from another round, or one issued to a
    /// different peer than the claimed one resolves to nothing, so the message is dropped. The other direction mirrors it:
    /// the token a peer offered me, recorded under the Photon-stamped source of its offer, is what I attach when I send
    /// to that peer, and I send nothing to it until it has offered one.
    /// </summary>
    public sealed class LinkTokens
    {
        public const int Size = 16;
        /// <summary>Offers kept per source at most (a stranger cannot grow the table by naming many rounds).</summary>
        public const int MaxEpochsPerSource = 4;

        private readonly Func<byte[]> _random;
        private readonly Dictionary<(int Epoch, int Peer), byte[]> _issued = new();
        private readonly Dictionary<(int Epoch, int Peer), byte[]> _offered = new();
        private readonly Dictionary<(int Epoch, int Peer), List<Action<byte[]>>> _waiting = new();
        private int _retiredBelow;

        public LinkTokens(Func<byte[]> random = null) => _random = random ?? SecureRandom;

        private static byte[] SecureRandom()
        {
            var b = new byte[Size];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return b;
        }

        /// <summary>Messages that a stranger sent or that named a round or peer with no token issued, dropped (diagnostics).</summary>
        public int Rejected { get; private set; }

        /// <summary>The token <paramref name="peer"/> must present on round <paramref name="epoch"/>: made once, then the same.</summary>
        public byte[] IssueFor(int epoch, int peer)
        {
            if (!_issued.TryGetValue((epoch, peer), out var token))
            {
                token = _random();
                if (token == null || token.Length != Size) throw new InvalidOperationException("token source must give " + Size + " bytes");
                _issued[(epoch, peer)] = token;
            }
            return (byte[])token.Clone();
        }

        /// <summary>
        /// A peer's offer, with <paramref name="authenticatedSource"/> the Photon-stamped sender of the message that carried
        /// it (never a field of the offer itself). Gives whatever was waiting on it to send.
        /// </summary>
        public bool AcceptOffer(int epoch, int authenticatedSource, byte[] token)
        {
            if (token == null || token.Length != Size || epoch < _retiredBelow || epoch <= 0) { Rejected++; return false; }
            _offered[(epoch, authenticatedSource)] = (byte[])token.Clone();
            var mine = _offered.Keys.Where(k => k.Peer == authenticatedSource).Select(k => k.Epoch).OrderBy(e => e).ToList();
            for (int i = 0; i < mine.Count - MaxEpochsPerSource; i++) _offered.Remove((mine[i], authenticatedSource));
            if (_waiting.TryGetValue((epoch, authenticatedSource), out var waiting))
            {
                _waiting.Remove((epoch, authenticatedSource));
                foreach (var send in waiting) send((byte[])token.Clone());
            }
            return true;
        }

        /// <summary>True once <paramref name="peer"/> has offered a token for <paramref name="epoch"/>.</summary>
        public bool HasOffer(int epoch, int peer) => _offered.ContainsKey((epoch, peer));

        /// <summary>Runs <paramref name="send"/> with the token to attach for <paramref name="peer"/>: now if it has offered one, else when it does.</summary>
        public void WithToken(int epoch, int peer, Action<byte[]> send)
        {
            if (epoch < _retiredBelow) return;
            if (_offered.TryGetValue((epoch, peer), out var token)) { send((byte[])token.Clone()); return; }
            if (!_waiting.TryGetValue((epoch, peer), out var list)) _waiting[(epoch, peer)] = list = new List<Action<byte[]>>();
            list.Add(send);
        }

        /// <summary>The peer a token was issued to on <paramref name="epoch"/>, comparing in constant time; false for any other token.</summary>
        public bool TryResolve(int epoch, byte[] buffer, int offset, int count, out int peer)
        {
            peer = -1;
            if (buffer == null || count < Size || offset < 0 || offset + Size > buffer.Length) { Rejected++; return false; }
            bool found = false;
            foreach (var entry in _issued)
            {
                if (entry.Key.Epoch != epoch) continue;
                int diff = 0;
                for (int i = 0; i < Size; i++) diff |= entry.Value[i] ^ buffer[offset + i];
                if (diff == 0 && !found) { found = true; peer = entry.Key.Peer; }
            }
            if (!found) Rejected++;
            return found;
        }

        /// <summary>Forgets every round before <paramref name="epoch"/>: tokens, offers and sends still waiting on one.</summary>
        public void RetireBelow(int epoch)
        {
            if (epoch <= _retiredBelow) return;
            _retiredBelow = epoch;
            foreach (var k in _issued.Keys.Where(k => k.Epoch < epoch).ToList()) _issued.Remove(k);
            foreach (var k in _offered.Keys.Where(k => k.Epoch < epoch).ToList()) _offered.Remove(k);
            foreach (var k in _waiting.Keys.Where(k => k.Epoch < epoch).ToList()) _waiting.Remove(k);
        }

        /// <summary>The payload of a message: the token, then <paramref name="body"/>.</summary>
        public static byte[] Frame(byte[] token, byte[] body)
        {
            var framed = new byte[Size + body.Length];
            Buffer.BlockCopy(token, 0, framed, 0, Size);
            Buffer.BlockCopy(body, 0, framed, Size, body.Length);
            return framed;
        }
    }
}
