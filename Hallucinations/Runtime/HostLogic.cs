// MIMESIS Hallucinations - host side: hello, request validation, relay, cooldowns, cleanup
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// Everything runs on the main thread: the admin dispatcher posts our command to the room's executor (VWorld.Update runs
// in Hub.FixedUpdate), and Settings.Changed comes from the Patches window (Update).
// State is keyed by VPlayer object (reference equality), never by actor id: ids are per room and can repeat. The
// cooldown is the exception: keyed by Steam id, so a dead player who reconnects (a new VPlayer) still waits it out.
//   _clients   VPlayer -> (protocol version, Caps, last hello) from its hello this level
//   _cooldown  Steam id (requester) -> host ms when it may send again (this level)
//   _active    VPlayer (target) -> (id, requester, weapon, spawned at, ttl)
// The host never trusts the client: every rule is checked here, whatever the client's tip said. It never spawns
// anything, never changes a stat except the debug radiation key (talk radiation is TalkRadiation.cs), never touches
// possession, and only ever sends to players whose game spoke this protocol to it.
// Fake players (the game's dummy players: FakePlayers, or its "spawndummyplayer" admin command) have no game. With
// Debug keys on, the host's own game plays their side (Proxy.cs): a slot's Game is whoever's game places it, runs it
// and decides its hits (the target itself, or the host for a fake player), and everything "to/from the target's game"
// goes to/comes from Game.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ReluProtocol;
using ReluProtocol.Enum;

namespace Hallucinations
{
    static class HostLogic
    {
        /// <summary>What a player's hello said (this level).</summary>
        struct ClientInfo
        {
            public int Version;
            public Caps Caps;
            public long LastHelloMs;
        }

        /// <summary>One hallucination on its way to / shown by a target.</summary>
        sealed class Slot
        {
            public int Id;
            /// <summary>The dead player who sent it; null for a debug self-spawn, or after they left the level.</summary>
            public VPlayer Requester;
            /// <summary>The requester's Steam id (its _cooldown key; stays valid after they leave).</summary>
            public ulong RequesterKey;
            public Weapon Weapon;
            public long SpawnedAt;
            public int TtlMs;
            public bool Debug;
            /// <summary>The requester's cooldown end this one started: a refund only gives back that one (a later
            /// hallucination's cooldown on another target stays).</summary>
            public long CooldownUntil;

            /// <summary>The living player who sees it.</summary>
            public VPlayer Target;
            /// <summary>The game that plays the target's side: the target's own, or the host's for a fake player.</summary>
            public VPlayer Game;
            public int Seed;
            /// <summary>The dead player who steers it (the requester), until they let go or leave; null = nobody.</summary>
            public VPlayer Controller;
            /// <summary>True while Controller moves it; false = the target's game runs it on its own (AI).</summary>
            public bool Controlled;
            /// <summary>The target placed it (Op.Placed): dead players can be shown it from now.</summary>
            public bool Placed;
            public Pose Pose;
            public int LastSeq;
            /// <summary>Dead players who were sent Show for it (they get its poses, attacks and end).</summary>
            public readonly HashSet<VPlayer> Shown = new HashSet<VPlayer>(ByRef.Instance);

            public long ExpiresAt => SpawnedAt + TtlMs + Const.SlotSlackMs;
        }

        static readonly Dictionary<VPlayer, ClientInfo> _clients = new Dictionary<VPlayer, ClientInfo>(ByRef.Instance);
        static readonly Dictionary<ulong, long> _cooldown = new Dictionary<ulong, long>();
        static readonly Dictionary<VPlayer, Slot> _active = new Dictionary<VPlayer, Slot>(ByRef.Instance);
        static readonly Dictionary<int, Slot> _byId = new Dictionary<int, Slot>();
        static readonly List<VPlayer> _viewers = new List<VPlayer>();
        static readonly List<VPlayer> _scratch = new List<VPlayer>();
        static readonly List<int> _targets = new List<int>();
        static readonly Weapon[] _pool = new Weapon[3];
        static readonly Random _rng = new Random();
        static int _nextId;
        static bool _subscribed;
        static VPlayer _leaving;    // OnRemovePlayer: the player leaving right now (still listed in its room)

        /// <summary>A game says hello once per level (re-sent every Const.HelloRetrySec until acked): hellos closer than
        /// this are dropped, so a modified client can't make the host re-ack the whole room on every packet.</summary>
        const long HelloMinGapMs = 1000;

        /// <summary>Settings.Changed -> OnSettingsChanged, once. Before the first command there is nobody to re-ack, so
        /// subscribing on the first one is enough.</summary>
        static void EnsureSubscribed()
        {
            if (_subscribed) return;
            _subscribed = true;
            Settings.Changed += OnSettingsChanged;
        }

        /// <summary>H1 registers this in VWorld.AdminCommandFuncs[Const.Command]. Always answers Success: the dispatcher
        /// sends the code back in an AdminCommandRes that every client drops, so refusals go in our own Result message.</summary>
        internal static MsgErrorCode OnCommand(VPlayer sender, AdminCommandArgs args)
        {
            if (!Control.HostReady || sender == null) return MsgErrorCode.Success;
            try
            {
                EnsureSubscribed();
                if (!Protocol.TryRead(args, out var m))
                {
                    Log.Trace($"Malformed command from {Name(sender)}");
                    return MsgErrorCode.Success;
                }
                Log.Trace($"{m.Op} from {Name(sender)} (v{m.Version})");
                switch (m.Op)
                {
                    case Op.Hello: OnHello(sender, m); break;
                    case Op.Request: OnRequest(sender, m); break;
                    case Op.End: OnEnd(sender, m); break;
                    case Op.DebugConta: OnDebugConta(sender, m); break;
                    case Op.Placed: OnPlaced(sender, m); break;
                    case Op.Drive: OnDrive(sender, m); break;
                    case Op.Attack: OnAttack(sender, m); break;
                    case Op.Release: OnRelease(sender, m); break;
                }
            }
            catch (Exception e) { Log.ErrorOnce("command", "Handling a command failed: " + e); }
            return MsgErrorCode.Success;
        }

        // =============================================================================================== hello (6.1)

        /// <summary>Records the player's capabilities and acks; when the set of players who show hallucinations in its
        /// level changed, the others get a fresh Ack (their target lists).</summary>
        static void OnHello(VPlayer p, in ClientMsg m)
        {
            if (m.Version != Const.ProtocolVersion)
            {
                Log.WarnOnce("hello-v" + m.Version,
                    $"{Name(p)} runs Hallucinations protocol {m.Version}, this host runs {Const.ProtocolVersion}: off for them " +
                    "(everyone needs the same version).");
                return;
            }
            long now = Control.NowMs;
            bool known = _clients.TryGetValue(p, out var old);
            if (known && now - old.LastHelloMs < HelloMinGapMs) return; // silently: no log line per flooded packet
            bool wasTarget = known && (old.Caps & Caps.Render) != 0;
            bool isTarget = (m.Caps & Caps.Render) != 0;
            _clients[p] = new ClientInfo { Version = m.Version, Caps = m.Caps, LastHelloMs = now };
            SendAck(p);
            if (isTarget != wasTarget) AckOthers(p.VRoom, p);
            // Hello is retried until acked: log the first one of this level only (or a change of capabilities).
            if (!known || old.Caps != m.Caps)
                Log.Info($"Hello from {Name(p)}: shows {(isTarget ? "yes" : "no")}, sends {((m.Caps & Caps.Request) != 0 ? "yes" : "no")}" +
                         (Settings.HostActive ? "" : " (hallucinations are off on this host)"));
        }

        // ====================================================================================== request (6.2)

        /// <summary>The validation chain, in order; the first failure is answered with a Result. On success: Spawn to the
        /// target, the slot, the requester's cooldown, Result Ok to the requester.</summary>
        static void OnRequest(VPlayer p, in ClientMsg m)
        {
            long now = Control.NowMs;
            var room = p.VRoom;

            if (!Settings.HostActive) { Refuse(p, m, Result.Disabled); return; }
            if (m.Version != Const.ProtocolVersion || !_clients.ContainsKey(p)) { Refuse(p, m, Result.BadRequest); return; }
            // The dungeon ended (success or failure): results screen and exit, every player protected (HarmBlocked).
            if (!(room is DungeonRoom) || room.IsSessionEnding) { Refuse(p, m, Result.NotInLevel); return; }
            // A deathmatch runs here (the Deathmatch patch says so through AppDomain data): a fight between players stays
            // fair, no fake attacker for the living to waste a shell on. The requester sees "Hallucinations are off".
            if (ReferenceEquals(AppDomain.CurrentDomain.GetData("MimesisPatches.FairFightRoom"), room)) { Refuse(p, m, Result.Disabled); return; }

            bool self = m.Target == p.ObjectID;
            if (self && !Settings.Debug) { Refuse(p, m, Result.BadRequest); return; }
            if (self && !p.IsAliveStatus()) { Refuse(p, m, Result.BadTarget); return; }
            // Really dead, as vanilla possession requires (not still loading in, nor in the death animation).
            if (!self && p.LifeCycle != VCreatureLifeCycle.Dead) { Refuse(p, m, Result.NotDead); return; }

            var t = self ? p : room.FindPlayerByObjectID(m.Target);
            if (t == null || !t.IsAliveStatus()) { Refuse(p, m, Result.BadTarget); return; }
            if (t.HarmBlocked) { Refuse(p, m, Result.NotInLevel); return; }
            var game = GameFor(t);
            if (game == null) { Refuse(p, m, Result.NoMod); return; }

            long conta = t.StatControlUnit?.GetCurrentConta() ?? 0L;
            if (!self && conta <= Settings.ThresholdConta) { Refuse(p, m, Result.LowRadiation); return; }   // "above": strictly
            if (!self && _cooldown.TryGetValue(p.SteamID, out long until) && until > now)
            {
                Refuse(p, m, Result.Cooldown, ClampMs(until - now));
                return;
            }
            if (_active.TryGetValue(t, out var busy))
            {
                if (now < busy.ExpiresAt) { Refuse(p, m, Result.Busy); return; }
                // Its end report never came (lost, or the target's game dropped it): forget it.
                Forget(busy, Outcome.Cancelled);
                Log.Trace($"Hallucination #{busy.Id} on {Name(t)} expired without an end report");
            }

            var weapon = self && m.Weapon >= 0 ? (Weapon)m.Weapon : Pick(Settings.Weapons);
            int id = NextId();
            int seed = _rng.Next(int.MinValue, int.MaxValue);
            int ttl = Settings.LifetimeMs;
            long cooldownUntil = self ? 0L : now + Settings.CooldownMs;
            // The dead player who sent it steers it (their hello said they can send, so they can steer); a debug
            // self-spawn runs on its own.
            var ctl = self ? null : p;

            SendTo(game, Protocol.Spawn(game.ObjectID, id, weapon, seed, ttl, self ? 0 : p.ObjectID, self, ctl != null ? ctl.ObjectID : 0,
                t.IsDummy ? t.ObjectID : 0));
            var slot = new Slot
            {
                Id = id,
                Requester = self ? null : p,
                RequesterKey = self ? 0UL : p.SteamID,
                Weapon = weapon,
                SpawnedAt = now,
                TtlMs = ttl,
                Debug = self,
                CooldownUntil = cooldownUntil,
                Target = t,
                Game = game,
                Seed = seed,
                Controller = ctl,
                Controlled = ctl != null,
            };
            _active[t] = slot;
            _byId[id] = slot;
            if (!self) _cooldown[p.SteamID] = cooldownUntil;
            SendTo(p, Protocol.Result(p.ObjectID, m.Nonce, Result.Ok, id, t.ObjectID, self ? 0 : ClampMs(Settings.CooldownMs)));
            Log.Info($"Hallucination #{id}: {Name(p)} -> {Name(t)} ({weapon}, radiation {Pct(conta)}%{(self ? ", debug" : "")}" +
                     $"{(t.IsDummy ? ", a fake player: " + Name(game) + "'s game plays its side" : "")})");
        }

        /// <summary>Whose game plays t's side, or null when none can: t's own when it said hello showing hallucinations;
        /// for a fake player, with Debug keys on, the host's (in t's level, showing hallucinations).</summary>
        static VPlayer GameFor(VPlayer t)
        {
            if (t == null) return null;
            if (!t.IsDummy) return _clients.TryGetValue(t, out var c) && (c.Caps & Caps.Render) != 0 ? t : null;
            if (!Settings.Debug) return null;
            foreach (var kv in _clients)
            {
                var h = kv.Key;
                if (h.IsHost && (kv.Value.Caps & Caps.Render) != 0 && ReferenceEquals(h.VRoom, t.VRoom) && InRoom(h, t.VRoom)) return h;
            }
            return null;
        }

        static void Refuse(VPlayer p, in ClientMsg m, Result r, int cooldownLeftMs = 0)
        {
            SendTo(p, Protocol.Result(p.ObjectID, m.Nonce, r, 0, m.Target, r == Result.Cooldown ? cooldownLeftMs : 0));
            Log.Trace($"Request #{m.Nonce} from {Name(p)} on actor {m.Target} refused: {r}" +
                      (r == Result.Cooldown ? $" ({cooldownLeftMs} ms left)" : ""));
        }

        // ========================================================================================== end (6.3)

        /// <summary>The target reports how its hallucination ended: free the slot, refund the requester's cooldown when it
        /// never really reached the player, and tell the requester.</summary>
        static void OnEnd(VPlayer p, in ClientMsg m)
        {
            if (!_byId.TryGetValue(m.Id, out var a) || !ReferenceEquals(a.Game, p))
            {
                Log.Trace($"End of hallucination #{m.Id} from {Name(p)} ignored (not one their game runs)");
                return;
            }
            var outcome = m.Outcome;
            if (outcome <= Outcome.None || outcome > Outcome.Missed) outcome = Outcome.Cancelled;   // unknown: no refund
            Forget(a, outcome);

            bool refunded = false;
            if (Outcomes.Refunds(outcome) && !a.Debug &&
                _cooldown.TryGetValue(a.RequesterKey, out long until) && until == a.CooldownUntil)
            {
                _cooldown.Remove(a.RequesterKey);
                refunded = true;
            }

            // The requester hears how it ended (a debug self-spawn: the target itself, its debug keys log it).
            var to = a.Debug ? p : a.Requester;
            if (to != null && InRoom(to, p.VRoom))
                SendTo(to, Protocol.OutcomeMsg(to.ObjectID, a.Id, outcome, a.Target.ObjectID));
            Log.Info($"Hallucination #{a.Id} on {Name(a.Target)} ended: {outcome}" + (refunded ? " (cooldown refunded)" : ""));
        }

        // ========================================================================= dead players: show and steer

        /// <summary>The target placed its hallucination: from now the dead players see it, and its controller steers it.</summary>
        static void OnPlaced(VPlayer p, in ClientMsg m)
        {
            if (!_byId.TryGetValue(m.Id, out var s) || !ReferenceEquals(s.Game, p) || s.Placed) return;
            s.Placed = true;
            s.Pose = m.Pose;
            ShowToDead(s);
            Log.Trace($"Hallucination #{s.Id} placed; shown to {s.Shown.Count} dead player(s)" +
                      (s.Controlled ? $", {Name(s.Controller)} steers it" : ""));
        }

        /// <summary>A pose from whoever moves it (the controller while it steers, else the target's game). Forwarded to the
        /// target's game (when the controller moves it) and to every dead player shown it, except the controller's own.
        /// For a fake player the host's game can be all three: its target's game, a dead player shown it and the
        /// controller, with a separate copy for each role, so only the steering copy is skipped.</summary>
        static void OnDrive(VPlayer p, in ClientMsg m)
        {
            if (!_byId.TryGetValue(m.Id, out var s) || !s.Placed || !IsMover(s, p)) return;
            if (m.Seq != 0 && m.Seq <= s.LastSeq) return; // older than the last one (reordered or replayed)
            s.LastSeq = m.Seq;
            s.Pose = m.Pose;
            ShowToDead(s); // players who died since it appeared see it from now
            if (s.Controlled) SendTo(s.Game, Protocol.DriveS(s.Game.ObjectID, s.Id, s.Pose, m.Seq));
            foreach (var v in s.Shown)
                if (!(s.Controlled && ReferenceEquals(v, p)) && InRoom(v, s.Target.VRoom)) SendTo(v, Protocol.DriveS(v.ObjectID, s.Id, s.Pose, m.Seq));
        }

        /// <summary>It attacks (whoever moves it says so): the target's game decides whether it lands; the others play it.</summary>
        static void OnAttack(VPlayer p, in ClientMsg m)
        {
            if (!_byId.TryGetValue(m.Id, out var s) || !s.Placed || !IsMover(s, p)) return;
            if (s.Controlled) SendTo(s.Game, Protocol.AttackS(s.Game.ObjectID, s.Id, m.Pose.Yaw));
            foreach (var v in s.Shown)
                if (!(s.Controlled && ReferenceEquals(v, p)) && InRoom(v, s.Target.VRoom)) SendTo(v, Protocol.AttackS(v.ObjectID, s.Id, m.Pose.Yaw));
            Log.Trace($"Hallucination #{s.Id} attacks ({(s.Controlled ? "steered by " + Name(p) : "on its own")})");
        }

        /// <summary>The controller lets go, or the target's game took it over (no poses from the controller): it runs on
        /// its own from now; the other side is told. (The host steering a fake player's hallucination is both: either way
        /// its game hears Release and both of its copies sort it out.)</summary>
        static void OnRelease(VPlayer p, in ClientMsg m)
        {
            if (!_byId.TryGetValue(m.Id, out var s) || !s.Controlled) return;
            if (ReferenceEquals(p, s.Controller)) StopSteering(s, "let go", tellTarget: true, tellController: false);
            else if (ReferenceEquals(p, s.Game)) StopSteering(s, "taken over by the target's game", tellTarget: false, tellController: true);
        }

        /// <summary>p is who moves s right now.</summary>
        static bool IsMover(Slot s, VPlayer p) =>
            s.Controlled ? ReferenceEquals(p, s.Controller) : ReferenceEquals(p, s.Game);

        static void StopSteering(Slot s, string why, bool tellTarget, bool tellController)
        {
            if (!s.Controlled) return;
            s.Controlled = false;
            s.LastSeq = 0; // the target's game counts its own poses
            if (tellTarget && InRoom(s.Game, s.Target.VRoom)) SendTo(s.Game, Protocol.ReleaseS(s.Game.ObjectID, s.Id));
            if (tellController && s.Controller != null && InRoom(s.Controller, s.Target.VRoom))
                SendTo(s.Controller, Protocol.ReleaseS(s.Controller.ObjectID, s.Id));
            Log.Info($"Hallucination #{s.Id}: {Name(s.Controller)} no longer steers it ({why})");
        }

        /// <summary>Show to every dead player (hello'd, showing hallucinations, in the target's level) not shown yet.</summary>
        static void ShowToDead(Slot s)
        {
            if (!s.Placed || s.Target == null) return;
            var room = s.Target.VRoom;
            _viewers.Clear();
            try
            {
                foreach (var kv in _clients)
                {
                    var v = kv.Key;
                    if ((kv.Value.Caps & Caps.Render) == 0 || ReferenceEquals(v, s.Target) || s.Shown.Contains(v)) continue;
                    if (!ReferenceEquals(v.VRoom, room) || !InRoom(v, room) || v.LifeCycle != VCreatureLifeCycle.Dead) continue;
                    _viewers.Add(v);
                }
                if (_viewers.Count == 0) return;
                long left = s.SpawnedAt + s.TtlMs - Control.NowMs;
                foreach (var v in _viewers)
                {
                    int ctl = s.Controlled && s.Controller != null ? s.Controller.ObjectID : 0;
                    SendTo(v, Protocol.Show(v.ObjectID, s.Id, s.Weapon, s.Seed, ClampMs(left), s.Target.ObjectID, ctl, s.Pose));
                    s.Shown.Add(v);
                }
            }
            finally { _viewers.Clear(); }
        }

        /// <summary>It's over (any reason): forget it, and the dead players shown it remove theirs.</summary>
        static void Forget(Slot s, Outcome outcome)
        {
            if (s.Target != null && _active.TryGetValue(s.Target, out var cur) && ReferenceEquals(cur, s)) _active.Remove(s.Target);
            _byId.Remove(s.Id);
            var room = s.Target != null ? s.Target.VRoom : null;
            foreach (var v in s.Shown)
                if (InRoom(v, room)) SendTo(v, Protocol.Gone(v.ObjectID, s.Id, outcome));
            s.Shown.Clear();
        }

        // =================================================================================== debug conta (6.4)

        /// <summary>Debug key: adds radiation to the sender (alive in a level), never past the talk cap.</summary>
        static void OnDebugConta(VPlayer p, in ClientMsg m)
        {
            if (m.Version != Const.ProtocolVersion || !Settings.Debug || !Settings.Enabled || !(p.VRoom is DungeonRoom) ||
                !p.IsAliveStatus() || p.HarmBlocked)
            {
                Log.Trace($"Debug radiation from {Name(p)} ignored (Debug keys off on the host, not alive in a level, or protected)");
                return;
            }
            var stat = p.StatControlUnit;
            if (stat == null) return;
            long max = Control.MaxConta;
            long add = max * Math.Max(1, Math.Min(m.Pct, Const.DebugContaStepPct)) / 100;
            long cap = Settings.TalkCapConta;
            long cur = stat.GetCurrentConta();
            long amount = Math.Min(add, cap - cur);
            if (amount <= 0)
            {
                Log.Info($"Debug: {Name(p)} radiation stays at {Pct(cur)}% (debug never goes past {Settings.TalkCapPct}%)");
                return;
            }
            stat.IncreaseConta(amount);
            Log.Info($"Debug: {Name(p)} radiation {Pct(cur)}% -> {Pct(stat.GetCurrentConta())}%");
        }

        // ================================================================================ settings, cleanup (6.5, 6.6)

        /// <summary>Settings.Changed (Patches window, main thread): every hello'd player still in a level gets the new
        /// settings.</summary>
        static void OnSettingsChanged()
        {
            if (!Control.HostReady || !Control.IsHost || Hub.s == null || Hub.s.vworld == null || _clients.Count == 0) return;
            try
            {
                _scratch.Clear();
                foreach (var p in _clients.Keys)
                    if (p.VRoom is DungeonRoom && InRoom(p, p.VRoom)) _scratch.Add(p);
                foreach (var p in _scratch) SendAck(p);
                Log.Trace($"Settings changed: Ack re-sent to {_scratch.Count} player(s)");
            }
            finally { _scratch.Clear(); }
        }

        /// <summary>H4: a new level. Every table is per level (new VPlayer objects per room anyway); ids keep counting.</summary>
        internal static void Reset()
        {
            _clients.Clear();
            _cooldown.Clear();
            _active.Clear();
            _byId.Clear();
        }

        /// <summary>H3 (prefix: the player is still in the room): forget them; if they could be a target, the others in
        /// that room get a fresh target list. Their cooldown stays (Steam id): a reconnect doesn't reset it.</summary>
        internal static void OnRemovePlayer(IVroom room, int actorId)
        {
            var p = room?.FindPlayerByObjectID(actorId);
            if (p == null) return;
            bool wasTarget = (_clients.TryGetValue(p, out var info) && (info.Caps & Caps.Render) != 0) || p.IsDummy;
            bool known = _clients.Remove(p);
            // The host leaving: the fake players' hallucinations its game ran go with it.
            _scratch.Clear();
            foreach (var s in _active.Values)
                if (ReferenceEquals(s.Game, p) && !ReferenceEquals(s.Target, p)) _scratch.Add(s.Target);
            foreach (var target in _scratch)
                if (_active.TryGetValue(target, out var orphan)) Forget(orphan, Outcome.Cancelled);
            _scratch.Clear();
            if (_active.TryGetValue(p, out var slot))
            {
                Forget(slot, Outcome.Cancelled);
                // The requester learns it was cut short (no refund: it did reach the player).
                var to = slot.Requester;
                if (to != null && InRoom(to, room))
                    SendTo(to, Protocol.OutcomeMsg(to.ObjectID, slot.Id, Outcome.Cancelled, p.ObjectID));
                Log.Info($"Hallucination #{slot.Id} on {Name(p)} dropped: they left");
            }
            foreach (var s in _active.Values)
            {
                if (ReferenceEquals(s.Requester, p)) s.Requester = null;
                // Its controller left: the target's game runs it on its own from now.
                if (ReferenceEquals(s.Controller, p))
                {
                    StopSteering(s, "they left", tellTarget: true, tellController: false);
                    s.Controller = null;
                }
                s.Shown.Remove(p);
            }
            if (wasTarget)
            {
                // Still in the room (a prefix): left out of the lists by hand.
                _leaving = p;
                try { AckOthers(room, p); }
                finally { _leaving = null; }
            }
            if (known) Log.Trace($"{Name(p)} left {room.GetType().Name}: forgotten");
        }

        // =============================================================================================== helpers

        /// <summary>The Ack: this host's settings and who in p's level shows hallucinations.</summary>
        static void SendAck(VPlayer p)
        {
            SendTo(p, Protocol.Ack(p.ObjectID, Settings.HostActive, Settings.ThresholdPct, Settings.CooldownSec,
                Settings.LifetimeSec, Settings.Weapons, Settings.Debug, TargetsOf(p.VRoom)));
        }

        /// <summary>A fresh Ack to every other hello'd player in room (the target list changed).</summary>
        static void AckOthers(IVroom room, VPlayer except)
        {
            if (room == null) return;
            _scratch.Clear();
            try
            {
                foreach (var o in _clients.Keys)
                    if (!ReferenceEquals(o, except) && ReferenceEquals(o.VRoom, room) && InRoom(o, room)) _scratch.Add(o);
                foreach (var o in _scratch) SendAck(o);
            }
            finally { _scratch.Clear(); }
        }

        /// <summary>The actor ids of the players in room whose hello said they show hallucinations, and of the fake players
        /// the host's game plays (Debug keys). The returned list is reused: Protocol.Ack reads it at once.</summary>
        static List<int> TargetsOf(IVroom room)
        {
            _targets.Clear();
            if (room == null) return _targets;
            foreach (var kv in _clients)
                if ((kv.Value.Caps & Caps.Render) != 0 && ReferenceEquals(kv.Key.VRoom, room) && InRoom(kv.Key, room))
                    _targets.Add(kv.Key.ObjectID);
            if (Settings.Debug)
            {
                try
                {
                    room.IterateAllPlayer(v =>
                    {
                        if (v != null && v.IsDummy && !ReferenceEquals(v, _leaving) && GameFor(v) != null) _targets.Add(v.ObjectID);
                    });
                }
                catch (Exception e) { Log.WarnOnce("fake-targets", "Listing the fake players failed: " + e.Message); }
            }
            return _targets;
        }

        /// <summary>H5: a fake player joined room: the players there get a fresh target list (with Debug keys on, it can be
        /// sent hallucinations).</summary>
        internal static void OnAddPlayer(IVroom room, VPlayer p)
        {
            if (p == null || !p.IsDummy || !Settings.Debug || !(room is DungeonRoom) || _clients.Count == 0) return;
            AckOthers(room, null);
            Log.Trace($"Fake player {Name(p)} joined: target lists re-sent");
        }

        /// <summary>p is still the player with its id in room (it didn't leave; ids can be reused by a later VPlayer).</summary>
        static bool InRoom(VPlayer p, IVroom room) =>
            p != null && room != null && ReferenceEquals(room.FindPlayerByObjectID(p.ObjectID), p);

        /// <summary>Uniform over the weapons in the pool; an empty pool means bare hands (the mimic's grab).</summary>
        static Weapon Pick(WeaponMask mask)
        {
            int n = 0;
            if ((mask & WeaponMask.Shotgun) != 0) _pool[n++] = Weapon.Shotgun;
            if ((mask & WeaponMask.Bomb) != 0) _pool[n++] = Weapon.Bomb;
            if ((mask & WeaponMask.Melee) != 0) _pool[n++] = Weapon.Melee;
            return n == 0 ? Weapon.Grab : _pool[_rng.Next(n)];
        }

        static int NextId()
        {
            _nextId = _nextId >= int.MaxValue - 1 ? 1 : _nextId + 1;
            return _nextId;
        }

        static int ClampMs(long ms) => ms <= 0 ? 0 : ms >= int.MaxValue ? int.MaxValue : (int)ms;

        /// <summary>Radiation in whole percent, for the log.</summary>
        internal static long Pct(long conta)
        {
            long max = Control.MaxConta;
            return max > 0 ? conta * 100 / max : 0;
        }

        /// <summary>One message to one player. The message is new for every send (see Protocol.S2C).</summary>
        internal static void SendTo(VPlayer p, IMsg msg)
        {
            if (p == null || msg == null) return;
            try { p.SendToMe(msg); }
            catch (Exception e) { Log.WarnOnce("send-" + msg.msgType, "Couldn't send to a player: " + e.Message); }
        }

        internal static string Name(VPlayer p) => p == null ? "?" : $"{p.ActorName}#{p.ObjectID}";

        /// <summary>Reference equality for VPlayer keys (.NET Framework has no ReferenceEqualityComparer).</summary>
        internal sealed class ByRef : IEqualityComparer<VPlayer>
        {
            public static readonly ByRef Instance = new ByRef();
            public bool Equals(VPlayer a, VPlayer b) => ReferenceEquals(a, b);
            public int GetHashCode(VPlayer p) => RuntimeHelpers.GetHashCode(p);
        }
    }
}
