// MIMESIS Patches - a special destination on the tram's screen (shared by Traitor and Deathmatch)
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// Compiled into each patch that offers one (each gets its own copy of this static state). The patch sets the fields
// at the top in its Plugin.Init and calls the On* methods from its own Harmony patches.
//   Host: when the tram rolls its two destinations (VWaitingRoom.RollDiceDungeon), one of them may become this
//   patch's special destination (Chance). Two patches never take the same one: claims go in AppDomain data, keyed by
//   the roll. The players see it on the tram's screen; whoever pulls the lever picks it or not, as any destination.
//   When the next level (DungeonRoom) is built from the tagged dungeon, it is this patch's: Active, and Started once
//   its arrival cutscene ends (the game turns its AI on). Players load in before their avatar exists on their game,
//   so messages to a player who just loaded are sent again a little later (SendSoon).
//   Wire (host -> every player): DebugInfoSig {actorID = the receiver's own avatar, debugInfo = Magic + "op=x;k=v;..."},
//   values URL-escaped. A vanilla game ignores it (debug info for a player actor does nothing); a game with this
//   patch reads it (ProtoActor.OnDebugInfoSig prefix). Ops here: "tags" (which destinations are special) and "banner"
//   (a message on screen); the patch adds its own.
//   Wire (every player -> host): AdminCommandReq {command = Command, args = "hello=1^p=<Protocol>"}, a few times in each
//   scene: "this game has the patch, and speaks this version of its messages". The host offers the destination only when
//   every player travelling said so with its own version (a player without it, or with messages it can't read, wouldn't
//   see the mark, their role or the rules); a vanilla host answers an error every game drops.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Mimic.Actors;
using ReluNetwork.ConstEnum;
using ReluProtocol;
using UnityEngine;

namespace MimesisPatches
{
    static class TramMode
    {
        // ---- set by the patch (Plugin.Init) ----
        internal static string Name = "Special";         // "Traitor", shown on the tram's screen
        internal static string Magic = "MDXMODE|";       // prefix of every message (unique per patch)
        internal static int Protocol = 1;                // version of the messages (hello, tags, the patch's own): raise it
                                                         // when they change; a player needs the host's to come along
        internal static string ColorHex = "#ff4040";
        internal static Func<int> ChancePct = () => 0;   // 0-100: how often one destination becomes this
        internal static int MinPlayers = 1;              // never offered with fewer players in the tram
        internal static Func<bool> CountFakePlayers = () => false; // FakePlayers' dummies count (testing)
        internal static Action<string> LogInfo = s => Debug.Log(s);
        internal static Action<DungeonRoom> LevelStarted;          // host: the special level's arrival is over
        internal static Action<ProtoActor, string, Dictionary<string, string>> ClientMessage; // client: a patch op

        const string ClaimKey = "MimesisPatches.TramSlots";

        /// <summary>Client -> host: "this game has the patch" (an admin command, as Hallucinations' hello: any host takes
        /// it, a vanilla one answers an error every game drops). Lower case: the game lower-cases command names.</summary>
        internal static string Command => "medovanx.tram." + Name.ToLowerInvariant();

        // ---- host state ----
        static readonly Dictionary<ulong, int> _protocols = new Dictionary<ulong, int>();   // said hello this session: Steam id -> its Protocol
        static readonly HashSet<string> _notesShown = new HashSet<string>();
        static string _missingNote;                // why this trip has no destination of ours (shown once to the host)
        static VWaitingRoom _taggedRoom;
        internal static int TaggedDungeonId;
        static int _taggedCrashId;                 // the crash-landing level of the tagged slot (the game may go there instead)
        internal static bool Pending;              // the lever was pulled with it selected (the "next stop" banner)
        static bool _forced;
        internal static int ForceNext;             // tests: 1 = the next level is ours whatever the choice
        internal static DungeonRoom Active;        // the current special level (null = none)
        internal static bool Started;

        internal static bool IsHost => Hub.s != null && Hub.s.pdata != null && Hub.s.pdata.ClientMode == NetworkClientMode.Host && Hub.s.vworld != null;

        // =========================================================================================== host: who has it

        /// <summary>VWorld.RegisterCommand postfix (a new session being hosted): our hello command.</summary>
        internal static void RegisterCommand(VWorld world)
        {
            _protocols.Clear();
            _notesShown.Clear();
            world.AdminCommandFuncs[Command] = OnHello;
        }

        static ReluProtocol.Enum.MsgErrorCode OnHello(VPlayer sender, AdminCommandArgs args)
        {
            if (sender == null || sender.SteamID == 0) return ReluProtocol.Enum.MsgErrorCode.Success;
            // No version in the hello: the first one (the games that sent none spoke it).
            int v = int.TryParse(args?["p"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) ? p : 1;
            if (!_protocols.TryGetValue(sender.SteamID, out int had) || had != v)
            {
                _protocols[sender.SteamID] = v;
                LogInfo(v == Protocol ? $"{sender.ActorName} has the {Name} patch"
                    : $"{sender.ActorName} has another version of the {Name} patch (its messages v{v}, the host's v{Protocol}): not for {Name.ToLowerInvariant()} levels");
            }
            return ReluProtocol.Enum.MsgErrorCode.Success;
        }

        /// <summary>This player's game has the patch, with the host's version of its messages (said hello this session);
        /// the host's own, and fake players when testing, count.</summary>
        internal static bool HasPatch(VPlayer p) =>
            p != null && (p.IsDummy ? CountFakePlayers() : p.IsHost || (_protocols.TryGetValue(p.SteamID, out int v) && v == Protocol));

        /// <summary>The players travelling with the team whose game hasn't said it has this patch, or said so with another
        /// version of its messages (names where known; other = a different version). Everyone must have it: a player
        /// without it wouldn't see the mark, their role or the rules. Travelling: the session's fixed players (the game
        /// sets them from the group that left the last room, and again from the tram's group when it leaves), plus
        /// whoever is in the given room now (someone who joined the tram since). A player connected but in no room (still
        /// loading, or waiting for the team, LateJoin) doesn't come along: not counted.</summary>
        static List<(string name, bool other)> WithoutPatch(IVroom room)
        {
            var missing = new List<(string name, bool other)>();
            var seen = new HashSet<ulong>();
            GameSessionInfo info = null;
            try { info = room.RoomManager.GetGameSessionInfo(); }
            catch (Exception) { /* below */ }
            void Check(ulong sid, string name)
            {
                if (sid == 0 || !seen.Add(sid)) return;
                if (info != null && info.TotalPlayerSteamIDs.TryGetValue(sid, out bool isHost) && isHost) return;   // the host
                if (!_protocols.TryGetValue(sid, out int v)) missing.Add((name ?? PlayerName(sid), false));
                else if (v != Protocol) missing.Add((name ?? PlayerName(sid), true));
            }
            if (info != null)
                foreach (var sid in info.FixedPlayerSteamIDs.Keys) Check(sid, null);
            room.IterateAllPlayer(p =>
            {
                if (p != null && !p.IsDummy && !p.IsHost) Check(p.SteamID, p.ActorName);
            });
            return missing;
        }

        /// <summary>"Ann doesn't seem to have the Traitor patch; Bob has a different version of it".</summary>
        static string Why(List<(string name, bool other)> without)
        {
            var none = without.FindAll(x => !x.other).ConvertAll(x => x.name);
            var other = without.FindAll(x => x.other).ConvertAll(x => x.name);
            var parts = new List<string>();
            if (none.Count > 0)
                parts.Add($"{string.Join(", ", none)} {(none.Count == 1 ? "doesn't seem" : "don't seem")} to have the {Name} patch");
            if (other.Count > 0)
                parts.Add($"{string.Join(", ", other)} {(other.Count == 1 ? "has" : "have")} a different version of {(none.Count > 0 ? "it" : "the " + Name + " patch")}");
            return string.Join("; ", parts);
        }

        static string MissingNote(List<(string name, bool other)> without) =>
            $"No {Name.ToLowerInvariant()} destinations: {Why(without)} " +
            $"({(without.Exists(x => x.other) ? "everyone needs the same version" : "everyone needs it")}).";

        static string PlayerName(ulong steamId)
        {
            string name = null;
            var rooms = Hub.s?.vworld?.VRoomManager?._vrooms;
            if (rooms != null)
                foreach (var r in rooms.Values)
                    r?.IterateAllPlayer(p => { if (name == null && p != null && p.SteamID == steamId) name = p.ActorName; });
            return name ?? "a player";
        }

        // =========================================================================================== client: hello

        static object _helloScene;
        static int _hellos;
        static float _nextHelloAt;
        static readonly float[] HelloAfterSec = { 1f, 4f, 12f };   // after each new scene's avatar appears

        /// <summary>Every frame (the patch's driver), when this game's player side works: tells the host this game has
        /// the patch, a few times in each new scene (the host keeps it for the session; packets can be lost).</summary>
        internal static void ClientTick()
        {
            var main = Hub.Main;
            if (main == null) return;
            if (!ReferenceEquals(main, _helloScene))
            {
                if (main.GetMyAvatar() == null) return;   // the main menu, or still loading
                _helloScene = main;
                _hellos = 0;
                _nextHelloAt = Time.unscaledTime + HelloAfterSec[0];
                RecheckIndoor(main);
            }
            if (_hellos >= HelloAfterSec.Length || Time.unscaledTime < _nextHelloAt) return;
            _hellos++;
            RecheckIndoor(main);   // again a moment later: where the avatar first stands can arrive after it appears
            if (_hellos < HelloAfterSec.Length) _nextHelloAt = Time.unscaledTime + HelloAfterSec[_hellos] - HelloAfterSec[_hellos - 1];
            string args = "hello=1^p=" + Protocol.ToString(CultureInfo.InvariantCulture);
            try { main.SendPacket(new AdminCommandReq { command = Command, args = args, reliable = true }); }
            catch (Exception e) { LogInfo("Telling the host about the patch failed: " + e.Message); }
        }

        /// <summary>A start inside the building (TramScatter): the game decides whether its avatar is indoors once, while
        /// the level loads (before the avatar exists: "outdoors"), and again only on a teleport, so it would draw the level
        /// as from outside (the building's rooms, and everyone in them, hidden). Decided again from where the avatar
        /// stands, with the game's own test; nothing changes when it already matches (a start in the tram, any level).
        /// Called as the avatar is placed (the patch's C3 hook on GamePlayScene.ResolveLevelLoadCompleteRes) and, in case
        /// that hook is off, from ClientTick a few times after the avatar appears.</summary>
        internal static void RecheckIndoor(GameMainBase main)
        {
            if (!(main is GamePlayScene level)) return;
            var avatar = level.GetMyAvatar();
            if (avatar == null || avatar.dead) return;
            try { level.MoveToIndoorOrOutdoor(level.CheckActorIsIndoor(avatar)); }
            catch (Exception e) { LogInfo("Checking whether you're indoors failed: " + e.Message); }
        }

        // =========================================================================================== host: the tram

        /// <summary>After the tram rolled its destinations (start of a trip, or a reroll).</summary>
        internal static void OnRoll(VWaitingRoom room)
        {
            _taggedRoom = room;
            TaggedDungeonId = 0;
            _taggedCrashId = 0;
            Pending = false; // a new trip: an older lever pull that never reached its level is void
            _forced = false;
            int first = room.FirstCandidateDungeonMasterID, second = room.SecondCandidateDungeonMasterID;
            if (first == 0 || second == 0) { SendTagsToRoom(room); return; }
            int pct = Mathf.Clamp(ChancePct(), 0, 100);
            string key = RollKey(room);
            var claims = ReadClaims(key);
            bool hadClaim = claims.Remove(Name);   // a roll again (same room and candidates): our earlier claim doesn't count
            // Testing with fake players (alone): they only join after the trip starts, so any player count goes then.
            int min = CountFakePlayers() ? 1 : MinPlayers;
            int players = PlayerCount(room);
            if (pct > 0 && players < min) LogInfo($"No {Name} destination this time: {players} player(s), it needs {min}");
            var without = pct > 0 ? WithoutPatch(room) : new List<(string name, bool other)>();
            _missingNote = null;
            if (without.Count > 0)
            {
                _missingNote = MissingNote(without);
                LogInfo(_missingNote);
            }
            if (pct > 0 && players >= min && without.Count == 0 && UnityEngine.Random.Range(0, 100) < pct)
            {
                var free = new List<int>();
                for (int slot = 0; slot < 2; slot++)
                {
                    int id = slot == 0 ? first : second;
                    if (slot == room.JackpotSlotIndex || claims.ContainsValue(id)) continue;
                    free.Add(id);
                }
                if (free.Count > 0)
                {
                    TaggedDungeonId = free[UnityEngine.Random.Range(0, free.Count)];
                    _taggedCrashId = TaggedDungeonId == first ? room.FirstCrashlandingDungeonMasterID : room.SecondCrashlandingDungeonMasterID;
                    claims[Name] = TaggedDungeonId;
                    WriteClaims(key, claims);
                    LogInfo($"The tram offers a {Name} destination (dungeon {TaggedDungeonId})");
                }
            }
            if (hadClaim && TaggedDungeonId == 0) WriteClaims(key, claims);   // not ours any more: free for the others
            SendTagsToRoom(room);
        }

        /// <summary>The lever was pulled: if it leaves with our destination selected, the players are told. (The game
        /// reads the selection again later, when it builds the level: OnDungeonCreated decides.)</summary>
        internal static void OnStartGame(VWaitingRoom room, ReluProtocol.Enum.MsgErrorCode result, int forcedDungeonId)
        {
            if (result != ReluProtocol.Enum.MsgErrorCode.Success) return;
            int chosen = forcedDungeonId != 0 ? forcedDungeonId : room.CurrentSelectedDungeonMasterID;
            _forced = ForceNext > 0;
            if (ForceNext > 0) ForceNext--;
            bool ours = _forced || (TaggedDungeonId != 0 && ReferenceEquals(room, _taggedRoom) && chosen == TaggedDungeonId && !room.BackToMaintenance);
            if (ours && !_forced)
            {
                // Someone joined the tram since the roll without the patch: a normal level after all (they would never
                // see the mark, their role or the rules). The mark goes too.
                var without = WithoutPatch(room);
                if (without.Count > 0)
                {
                    ours = false;
                    TaggedDungeonId = 0;
                    _taggedCrashId = 0;
                    SendTagsToRoom(room);
                    string note = $"Not a {Name.ToLowerInvariant()} job after all: {Why(without)}. A normal level.";
                    LogInfo(note);
                    SendRoom(room, "banner", ("t", Name.ToUpperInvariant()), ("s", note), ("c", "#c0c0c0"), ("d", "7"));
                }
            }
            Pending = ours;
            if (ours)
            {
                LogInfo($"The tram leaves for the {Name} destination");
                SendRoom(room, "banner", ("t", Name.ToUpperInvariant()), ("s", "Next stop: the " + Name.ToLowerInvariant() + " job"), ("c", ColorHex), ("d", "5"));
            }
        }

        /// <summary>A level is created (VWorld builds the next DungeonRoom when the tram leaves): ours if it is the
        /// tagged dungeon (or its crash-landing level), whatever was selected when the lever moved (the selection can
        /// still change in between).</summary>
        internal static void OnDungeonCreated(DungeonRoom room)
        {
            int id = 0;
            try { id = (room.Property as DungeonProperty)?.DungeonMasterID ?? 0; }
            catch (Exception) { /* below */ }
            // A crash-landing level can be shared by both candidates: that one only counts if ours was selected at the lever.
            bool ours = _forced || (TaggedDungeonId != 0 && id != 0 && (id == TaggedDungeonId || (Pending && id == _taggedCrashId)));
            if (ours != Pending && TaggedDungeonId != 0)
                LogInfo($"The destination changed after the lever: dungeon {id} is {(ours ? "" : "not ")}the {Name} destination");
            if (ours && !_forced)
            {
                // The last word: the game has just set who goes there (its fixed players). Anyone without the patch
                // (joined after the lever): a normal level.
                var without = WithoutPatch(room);
                if (without.Count > 0)
                {
                    ours = false;
                    LogInfo($"Not a {Name.ToLowerInvariant()} level after all: {Why(without)}");
                }
            }
            Active = ours ? room : null;
            // Its players start inside the building (TramScatter), not in the tram: its monsters wait until the arrival is
            // over (the game pauses them only from the arrival cutscene on, while everyone waits for the slowest to load
            // with their input locked). The arrival turns them on again (GameActionEnableAI: OnAiEnabled, Started).
            if (ours) room.EnableAIControl(false);
            Started = false;
            Pending = false;
            _forced = false;
            TaggedDungeonId = 0;
            _taggedCrashId = 0;
            _taggedRoom = null;
        }

        /// <summary>A room turned its AI on: for our level, that's the end of its arrival.</summary>
        internal static void OnAiEnabled(IVroom room, bool enable)
        {
            if (!enable || Started || Active == null || !ReferenceEquals(room, Active)) return;
            Started = true;
            LogInfo($"{Name} level started");
            LevelStarted?.Invoke(Active);
        }

        /// <summary>A player finished loading a room: the tram's tags for them.</summary>
        internal static void OnPlayerLoaded(IVroom room, VPlayer player)
        {
            if (!(room is VWaitingRoom w) || !ReferenceEquals(w, _taggedRoom) || player == null) return;
            SendSoon(room, player, "tags", () => ReferenceEquals(room, _taggedRoom) ? TagFields() : null);
            // The host learns why there's no destination of ours (once per reason per session).
            string note = _missingNote;
            if (player.IsHost && note != null && _notesShown.Add(note))
                room.AddEventTimer(() =>
                {
                    try { if (ReferenceEquals(room.FindPlayerByObjectID(player.ObjectID), player)) Banner(room, player, Name.ToUpperInvariant(), note, "#c0c0c0", 8f); }
                    catch (Exception e) { LogInfo("Telling the host failed: " + e.Message); }
                }, 2500);
        }

        static void SendTagsToRoom(VWaitingRoom room) => room.IterateAllPlayer(p => Send(p, "tags", TagFields()));

        static (string key, string value)[] TagFields() => new[] { ("ids", TaggedDungeonId.ToString(CultureInfo.InvariantCulture)) };

        /// <summary>Now, and again 1.5 s and 5 s later while they're still in the room. "Loaded" on the server can come
        /// before the player's avatar exists on their game, and a message for a missing avatar is dropped there; a
        /// repeat changes nothing for a game that already has it. fields is read at each send (null = send nothing).</summary>
        internal static void SendSoon(IVroom room, VPlayer p, string op, Func<(string key, string value)[]> fields)
        {
            if (room == null || p == null) return;
            var now = fields();
            if (now != null) Send(p, op, now);
            foreach (long ms in new long[] { 1500, 5000 })
            {
                room.AddEventTimer(() =>
                {
                    try
                    {
                        if (!ReferenceEquals(room.FindPlayerByObjectID(p.ObjectID), p)) return;
                        var f = fields();
                        if (f != null) Send(p, op, f);
                    }
                    catch (Exception e) { LogInfo("A repeated message failed: " + e.Message); }
                }, ms);
            }
        }

        /// <summary>The session's players. The tram rolls before anyone is in it (the host's tram scene calls
        /// VRoomManager.InitWaitingRoom while everyone is between rooms), so the session's own list counts (every
        /// connected player), or the players in rooms if that's more; fake players (not in the session's list) only
        /// when testing.</summary>
        static int PlayerCount(IVroom tram)
        {
            int session = 0;
            try { session = tram.RoomManager.GetGameSessionInfo().TotalPlayerSteamIDs.Count; }
            catch (Exception) { /* below */ }
            var seen = new HashSet<ulong>();
            int dummies = 0;
            var rooms = Hub.s?.vworld?.VRoomManager?._vrooms;
            void Count(IVroom r) => r?.IterateAllPlayer(p =>
            {
                if (p == null) return;
                if (p.IsDummy) { if (CountFakePlayers()) dummies++; }
                else seen.Add(p.SteamID != 0 ? p.SteamID : (ulong)p.UID);
            });
            if (rooms != null) foreach (var r in rooms.Values) Count(r);
            else Count(tram);
            return Math.Max(session, seen.Count) + dummies;
        }

        static string RollKey(VWaitingRoom r) =>
            RuntimeHelpers.GetHashCode(r).ToString(CultureInfo.InvariantCulture) + ":" + r.FirstCandidateDungeonMasterID + ":" + r.SecondCandidateDungeonMasterID;

        /// <summary>The destinations other patches took on this roll (name -> dungeon id).</summary>
        static Dictionary<string, int> ReadClaims(string key)
        {
            var map = new Dictionary<string, int>();
            if (!(AppDomain.CurrentDomain.GetData(ClaimKey) is string s) || !s.StartsWith(key + "|", StringComparison.Ordinal)) return map;
            foreach (var part in s.Substring(key.Length + 1).Split(','))
            {
                int at = part.IndexOf('@');
                if (at > 0 && int.TryParse(part.Substring(at + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) map[part.Substring(0, at)] = id;
            }
            return map;
        }

        static void WriteClaims(string key, Dictionary<string, int> claims)
        {
            var sb = new StringBuilder(key).Append('|');
            bool first = true;
            foreach (var kv in claims)
            {
                if (!first) sb.Append(',');
                sb.Append(kv.Key).Append('@').Append(kv.Value.ToString(CultureInfo.InvariantCulture));
                first = false;
            }
            AppDomain.CurrentDomain.SetData(ClaimKey, sb.ToString());
        }

        // =========================================================================================== host: helpers

        /// <summary>A message to one player (to their own avatar's id in their room).</summary>
        internal static void Send(VPlayer p, string op, params (string key, string value)[] fields)
        {
            if (p == null || p.IsDummy) return;
            var sb = new StringBuilder(Magic).Append("op=").Append(op);
            foreach (var f in fields) sb.Append(';').Append(f.key).Append('=').Append(Uri.EscapeDataString(f.value ?? ""));
            try
            {
                p.SendToMe(new DebugInfoSig
                {
                    actorID = p.ObjectID,
                    debugInfo = sb.ToString(),
                    hitCheckDrawInfos = new List<HitCheckDrawInfo>(),
                    reliable = true,
                });
            }
            catch (Exception e) { LogInfo("Sending to " + p.ActorName + " failed: " + e.Message); }
        }

        internal static void SendRoom(IVroom room, string op, params (string key, string value)[] fields) =>
            room?.IterateAllPlayer(p => Send(p, op, fields));

        /// <summary>A banner on screen: to one player (p) or everyone in the room (p null).</summary>
        internal static void Banner(IVroom room, VPlayer p, string title, string sub, string colorHex, float seconds)
        {
            var f = new[] { ("t", title), ("s", sub), ("c", colorHex ?? ColorHex), ("d", seconds.ToString("0.#", CultureInfo.InvariantCulture)) };
            if (p != null) Send(p, "banner", f);
            else SendRoom(room, "banner", f);
        }

        /// <summary>Money for the whole team (the session's money lives in the maintenance room, which waits while
        /// everyone is out: it reaches them back at the station).</summary>
        internal static bool AddTeamMoney(int amount)
        {
            var rooms = Hub.s?.vworld?.VRoomManager?._vrooms;
            if (rooms == null || amount == 0) return false;
            foreach (var r in rooms.Values)
            {
                if (r is MaintenanceRoom m)
                {
                    m.AddCurrency(amount);
                    return true;
                }
            }
            return false;
        }

        /// <summary>An amount that matters at this stage: fraction of the stage's repair cost, at least min.</summary>
        internal static int StageMoney(IVroom room, float fraction, int min)
        {
            int quota = 0;
            try { quota = room.RoomManager.GetGameSessionInfo().GetCurrencyThreshold(); }
            catch (Exception) { /* below */ }
            return Mathf.Max(min, Mathf.RoundToInt(quota * fraction));
        }

        /// <summary>The players that take part: alive, and fake players only for testing.</summary>
        internal static List<VPlayer> Players(IVroom room, bool aliveOnly)
        {
            var list = new List<VPlayer>();
            room?.IterateAllPlayer(p =>
            {
                if (p == null || (p.IsDummy && !CountFakePlayers())) return;
                if (aliveOnly && !p.IsAliveStatus()) return;
                list.Add(p);
            });
            list.Sort((a, b) => a.ObjectID.CompareTo(b.ObjectID));
            return list;
        }

        // =========================================================================================== client

        static readonly HashSet<int> _clientTags = new HashSet<int>();
        static object _tagsScene;   // the scene the tags came in (that tram's): another tram, or another host's, has none
        static readonly Dictionary<UIPrefab_NextMap, (int id, bool jackpot, bool rainy)> _cards = new Dictionary<UIPrefab_NextMap, (int id, bool jackpot, bool rainy)>();

        /// <summary>Client inbox (ProtoActor.OnDebugInfoSig prefix): true when the message was ours (swallow it).</summary>
        internal static bool OnDebugInfo(ProtoActor receiver, DebugInfoSig sig)
        {
            if (sig == null || sig.debugInfo == null || !sig.debugInfo.StartsWith(Magic, StringComparison.Ordinal)) return false;
            try
            {
                var map = new Dictionary<string, string>();
                foreach (var part in sig.debugInfo.Substring(Magic.Length).Split(';'))
                {
                    int eq = part.IndexOf('=');
                    if (eq > 0) map[part.Substring(0, eq)] = Uri.UnescapeDataString(part.Substring(eq + 1));
                }
                map.TryGetValue("op", out string op);
                switch (op)
                {
                    case "tags":
                        _clientTags.Clear();
                        _tagsScene = Hub.Main;
                        if (map.TryGetValue("ids", out string ids))
                            foreach (var s in ids.Split('.'))
                                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && id != 0) _clientTags.Add(id);
                        RefreshCards();
                        break;
                    case "banner":
                        map.TryGetValue("t", out string t);
                        map.TryGetValue("s", out string sub);
                        map.TryGetValue("c", out string c);
                        float d = map.TryGetValue("d", out string ds) && float.TryParse(ds, NumberStyles.Float, CultureInfo.InvariantCulture, out float dv) ? dv : 5f;
                        TramModeBanner.Show(t, sub, c, d);
                        break;
                    default:
                        ClientMessage?.Invoke(receiver, op, map);
                        break;
                }
            }
            catch (Exception e) { LogInfo("A message from the host couldn't be read: " + e.Message); }
            return true;
        }

        /// <summary>After a tram destination card shows a dungeon (UIPrefab_NextMap.ApplyMap postfix).</summary>
        internal static void OnCardApplied(UIPrefab_NextMap card, int dungeonId, bool jackpot, bool rainy)
        {
            if (card == null) return;
            _cards[card] = (dungeonId, jackpot, rainy);
            Decorate(card, dungeonId);
        }

        /// <summary>The tags changed: every card shows its dungeon again (ApplyMap resets the text; its postfix decorates).</summary>
        static void RefreshCards()
        {
            var gone = new List<UIPrefab_NextMap>();
            foreach (var kv in new List<KeyValuePair<UIPrefab_NextMap, (int id, bool jackpot, bool rainy)>>(_cards))
            {
                if (kv.Key == null) { gone.Add(kv.Key); continue; }
                try { kv.Key.ApplyMap(kv.Value.id, kv.Value.jackpot, kv.Value.rainy); }
                catch (Exception) { Decorate(kv.Key, kv.Value.id); }
            }
            foreach (var g in gone) _cards.Remove(g);
        }

        static void Decorate(UIPrefab_NextMap card, int dungeonId)
        {
            if (_clientTags.Count > 0 && !ReferenceEquals(_tagsScene, Hub.Main)) _clientTags.Clear(); // an older tram's
            if (!_clientTags.Contains(dungeonId)) return;
            string tag = $"<color={ColorHex}>{Name.ToUpperInvariant()}</color>  ";
            foreach (var text in new[] { card.UE_TMP_Text_NextMap, card.UE_Text_JackpotMap })
            {
                if (text == null || text.text == null || text.text.Contains(tag)) continue;
                text.richText = true;
                text.text = tag + text.text;
            }
        }
    }

    /// <summary>Whether the game shows its own HUD right now: not hidden for one of its special screens, and its in-game
    /// menu isn't open. Lines that stay on screen (a role, the rules) show only then; banners show over special screens
    /// too (they carry news, e.g. who the traitor was, at the end of a level) and wait while the menu is open.</summary>
    static class GameHud
    {
        static float _checkedAt = -1f;
        static bool _visible = true;

        /// <summary>The game's in-game menu (Esc) is open.</summary>
        internal static bool MenuOpen
        {
            get
            {
                try { return Hub.s?.uiman != null && Hub.s.uiman.isGameMenuOpen; }
                catch (Exception) { return false; }
            }
        }

        internal static bool Visible
        {
            get
            {
                // OnGUI runs several times a frame: look again only a few times a second.
                if (Time.unscaledTime - _checkedAt < 0.25f) return _visible;
                _checkedAt = Time.unscaledTime;
                _visible = Check();
                return _visible;
            }
        }

        static bool Check()
        {
            try
            {
                if (MenuOpen) return false;
                var hud = Hub.Main != null ? Hub.Main.ingameui : null;
                if (hud == null) return true;   // not known (another scene type): shown, as before
                if (!hud.isActiveAndEnabled) return false;
                var root = hud.UE_rootNode;
                var group = root != null ? root.GetComponent<CanvasGroup>() : null;
                return group == null || group.alpha > 0.01f;
            }
            catch (Exception) { return true; }
        }
    }

    /// <summary>Big messages on screen (legacy GUI: no game UI prefab to break). One at a time: within this patch the
    /// newest replaces the one on screen; another patch's banner (Traitor and Deathmatch each compile this class) waits
    /// its turn instead (AppDomain data SlotKey says whose banner is up, until when), so two never overlap.</summary>
    sealed class TramModeBanner : MonoBehaviour
    {
        const string SlotKey = "MimesisPatches.BannerSlot";   // "<patch>|<until, in unscaled time>"
        const int MaxWaiting = 4;

        static TramModeBanner _instance;
        static bool _failed;
        string _title, _sub;
        Color _color = Color.white;
        float _until, _shownAt, _claimedUntil = -1f;
        GUIStyle _big, _small;
        readonly Queue<(string title, string sub, string color, float seconds)> _waiting = new Queue<(string, string, string, float)>();

        internal static void Show(string title, string sub, string colorHex, float seconds)
        {
            if (_instance == null)
            {
                var go = new GameObject("MedovanxBanner." + TramMode.Name);
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<TramModeBanner>();
            }
            TramMode.LogInfo($"Banner: {title} - {sub}");
            if (OtherShowing())
            {
                if (_instance._waiting.Count < MaxWaiting) _instance._waiting.Enqueue((title, sub, colorHex, seconds));
                return;
            }
            _instance.Display(title, sub, colorHex, seconds);
        }

        void Display(string title, string sub, string colorHex, float seconds)
        {
            _title = title ?? "";
            _sub = sub ?? "";
            if (!ColorUtility.TryParseHtmlString(colorHex ?? "", out _color)) _color = Color.white;
            _shownAt = Time.unscaledTime;
            _until = Time.unscaledTime + Mathf.Clamp(seconds, 1f, 30f);
            Claim();
        }

        void Claim()
        {
            _claimedUntil = _until;
            AppDomain.CurrentDomain.SetData(SlotKey, TramMode.Name + "|" + _until.ToString("R", CultureInfo.InvariantCulture));
        }

        /// <summary>Another patch's banner is on screen now.</summary>
        static bool OtherShowing()
        {
            if (!(AppDomain.CurrentDomain.GetData(SlotKey) is string s)) return false;
            int bar = s.LastIndexOf('|');
            if (bar <= 0 || s.Substring(0, bar) == TramMode.Name) return false;
            return float.TryParse(s.Substring(bar + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float until) && until > Time.unscaledTime;
        }

        void Update()
        {
            try
            {
                float now = Time.unscaledTime;
                // While the game's menu is open the message waits, hidden: it shows in full once the menu is closed.
                if (_until > now && GameHud.MenuOpen)
                {
                    _until += Time.unscaledDeltaTime;
                    _shownAt += Time.unscaledDeltaTime;
                }
                if (_until > now && _until - _claimedUntil > 0.2f) Claim();   // the slot stays ours as long as it shows
                if (_waiting.Count > 0 && now >= _until && !OtherShowing())
                {
                    var next = _waiting.Dequeue();
                    Display(next.title, next.sub, next.color, next.seconds);
                }
            }
            catch (Exception e) { Failed(e); }
        }

        void OnGUI()
        {
            if (_failed || Time.unscaledTime >= _until || string.IsNullOrEmpty(_title + _sub) || GameHud.MenuOpen) return;
            try
            {
                if (_big == null)
                {
                    _big = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = true };
                    _small = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
                }
                float scale = Screen.height / 1080f;
                _big.fontSize = Mathf.RoundToInt(46 * scale);
                _small.fontSize = Mathf.RoundToInt(24 * scale);
                float fade = Mathf.Clamp01((_until - Time.unscaledTime) / 0.8f) * Mathf.Clamp01((Time.unscaledTime - _shownAt) / 0.25f);
                float w = Screen.width * 0.8f, x = (Screen.width - w) / 2f, y = Screen.height * 0.18f;
                DrawShadowed(new Rect(x, y, w, 70 * scale), _title, _big, new Color(_color.r, _color.g, _color.b, fade));
                DrawShadowed(new Rect(x, y + 70 * scale, w, 80 * scale), _sub, _small, new Color(1f, 1f, 1f, 0.92f * fade));
            }
            catch (Exception e) { Failed(e); }
        }

        /// <summary>Drawing failed (OnGUI runs several times a frame): said once, then no more banners.</summary>
        static void Failed(Exception e)
        {
            if (_failed) return;
            _failed = true;
            TramMode.LogInfo("Banners turned off after an error: " + e);
        }

        static void DrawShadowed(Rect r, string text, GUIStyle style, Color color)
        {
            if (string.IsNullOrEmpty(text)) return;
            var old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, color.a * 0.8f);
            GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), text, style);
            GUI.color = color;
            GUI.Label(r, text, style);
            GUI.color = old;
        }
    }
}
