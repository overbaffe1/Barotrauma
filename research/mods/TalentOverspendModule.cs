// ============================================================================
//  TalentOverspendModule.cs — Find #1 (flagship): full talent tree on a spent budget
// ============================================================================
//
//  BUG (verified against source):
//    Server read path: CharacterNetworking.cs (ServerSource) -> EventType.UpdateTalents
//      CanManageTalents(c)              // for own character ALWAYS true
//      ushort talentCount = msg.ReadUInt16();
//      foreach: u32 UintIdentifier -> TalentTree.IsViableTalentForCharacter(this, id, talentSelection)
//               -> GiveTalent(id); talentSelection.Add(id);
//
//    The budget gate inside TalentTree.IsViableTalentForCharacter (line ~131):
//          character.Info.GetTotalTalentPoints() - selectedTalents.Count <= 0  ->  not viable
//
//    Three stacked flaws:
//      1) GetTotalTalentPoints() = level + AdditionalTalentPoints = TOTAL EARNED points,
//         not the remaining balance. The correct accessor, GetAvailableTalentPoints()
//         (subtracts already-unlocked talents), is never used by the server.
//      2) `selectedTalents` is a fresh packet-local list rebuilt on EVERY UpdateTalents
//         message. It never contains talents unlocked by previous packets.
//         => the budget resets to the full earned amount on every packet.
//      3) Character.GiveTalent() performs no point check at all.
//    Extra gift: already-unlocked talents short-circuit viability ("backwards compat"
//    early-out), so re-sending them re-validates for free.
//
//  EXPLOIT:
//    Send repeated UpdateTalents events, each containing a structurally valid
//    in-packet chain of talents (tier prerequisites INCLUDED IN THE SAME PACKET,
//    because requirements are also evaluated against the packet-local list).
//    Each packet unlocks up to (level + additional points) talents. Repeat until
//    the whole job tree is unlocked. In campaign, UnlockedTalents persist between
//    rounds -> permanent build, not a visual effect.
//
//  HOW THIS MODULE WORKS:
//    - Harmony prefix on Character.ClientEventWrite: when we queue a payload, the
//      outgoing UpdateTalents event is written with OUR talent list instead of the
//      vanilla characterTalents dump.
//      Wire format (must mirror vanilla byte-for-byte):
//        WriteRangedInteger((int)EventType.UpdateTalents, MinValue, MaxValue)
//        WriteUInt16(count); WriteUInt32(prefab.UintIdentifier) * count
//    - The tree (internal classes TalentTree/TalentSubTree/TalentOption) is read via
//      reflection and mirrored into plain data (stages, required counts, max counts,
//      required/blocked subtrees).
//    - A faithful server-side replay simulator (budget + viability in exact vanilla
//      order) validates each crafted packet before sending.
//    - Packet builder: per target subtree, fill required-tree stages, fill earlier
//      stages of the own subtree, then append every unconfirmed talent. Conflicts
//      (subtree A blocks subtree B, both targeted) defer B to a later packet.
//    - Confirmation = server echo: the server's GiveTalent emits the talent back and
//      the local Info.UnlockedTalents grows. Timeout/retry + stall detection included.
//
//  USAGE (hub integration):
//      TalentOverspendModule.Start();        // arm (also self-patches Character.Update)
//      TalentOverspendModule.Stop();         // disarm
//      TalentOverspendModule.Update();       // optional if the hub has its own pump;
//                                            // otherwise the Character.Update postfix drives it
//  Progress is printed to the local console (DebugConsole.NewMessage).
// ============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    public static class TalentOverspendModule
    {
        // ------------------------------------------------------------------ state

        public static bool Enabled { get; private set; }
        public static string Status { get; private set; } = "idle";

        private static Harmony _harmony;
        private static bool _patched;

        private static List<uint> _pendingPayload;      // queued wire payload for the write-prefix

        private enum Phase { Idle, WaitEcho }
        private static Phase _phase;

        private static List<Identifier> _plan;          // dependency-ordered plan
        private static int _lastUnlockedCount;
        private static double _lastSendTime;
        private static int _emptyStreak;
        private static int _packetsSent;
        private static Identifier _planJobId;

        private const double SendInterval = 0.25;
        private const double EchoTimeout = 4.0;
        private const int MaxEmptyStreak = 3;

        // --------------------------------------------------- reflection (internal types)

        private static Type _tTalentTree, _tSubTree, _tOption;
        private static FieldInfo _fiJobTrees;           // static PrefabCollection<TalentTree> JobTalentTrees
        private static FieldInfo _fiSubTrees;           // ImmutableArray<TalentSubTree> TalentSubTrees
        private static FieldInfo _fiStages;             // ImmutableArray<TalentOption> TalentOptionStages
        private static FieldInfo _fiRequiredTrees;      // ImmutableHashSet<Identifier> RequiredTrees
        private static FieldInfo _fiBlockedTrees;       // ImmutableHashSet<Identifier> BlockedTrees
        private static PropertyInfo _piSubId;           // TalentSubTree.Identifier
        private static PropertyInfo _piOptionIds;       // TalentOption.TalentIdentifiers
        private static FieldInfo _fiRequiredTalents;    // TalentOption.RequiredTalents
        private static FieldInfo _fiMaxChosen;          // TalentOption.MaxChosenTalents
        private static MethodInfo _miTryGet;            // PrefabCollection<TalentTree>.TryGet

        // Build-dependent members (NOT present in every game build — resolved optionally):
        private static MethodInfo _miIsTalentLocked;    // Character.IsTalentLocked(Identifier) — optional
        private static Type _tTalentPrefab;             // internal Barotrauma.TalentPrefab
        private static FieldInfo _fiTalentPrefabs;      // static TalentPrefab.TalentPrefabs (internal class)
        private static PropertyInfo _piPrefabId;        // Prefab.Identifier (public base property)
        private static PropertyInfo _piUintId;          // PrefabWithUintIdentifier.UintIdentifier (public)
        private static Dictionary<Identifier, uint> _uintById;   // talent id -> wire uint

        private static bool EnsureReflection()
        {
            if (_tTalentTree != null) { return true; }

            var asm = typeof(CharacterInfo).Assembly;
            _tTalentTree = asm.GetType("Barotrauma.TalentTree", throwOnError: false);
            _tSubTree = asm.GetType("Barotrauma.TalentSubTree", throwOnError: false);
            _tOption = asm.GetType("Barotrauma.TalentOption", throwOnError: false);
            if (_tTalentTree == null || _tSubTree == null || _tOption == null)
            {
                Status = "reflection failed: talent tree types not found";
                return false;
            }

            _fiJobTrees = _tTalentTree.GetField("JobTalentTrees", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            _fiSubTrees = _tTalentTree.GetField("TalentSubTrees", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _fiStages = _tSubTree.GetField("TalentOptionStages", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _fiRequiredTrees = _tSubTree.GetField("RequiredTrees", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _fiBlockedTrees = _tSubTree.GetField("BlockedTrees", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _piSubId = _tSubTree.GetProperty("Identifier", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _piOptionIds = _tOption.GetProperty("TalentIdentifiers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _fiRequiredTalents = _tOption.GetField("RequiredTalents", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _fiMaxChosen = _tOption.GetField("MaxChosenTalents", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (_fiJobTrees == null || _fiSubTrees == null || _fiStages == null || _fiRequiredTrees == null ||
                _fiBlockedTrees == null || _piSubId == null || _piOptionIds == null ||
                _fiRequiredTalents == null || _fiMaxChosen == null)
            {
                Status = "reflection failed: member not found";
                return false;
            }

            _miTryGet = _fiJobTrees.FieldType.GetMethod("TryGet", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (_miTryGet == null) { Status = "reflection failed: PrefabCollection.TryGet not found"; return false; }

            // ---- optional members (missing in some game builds — degrade gracefully) ----
            _miIsTalentLocked = typeof(Character).GetMethod(
                "IsTalentLocked",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(Identifier) }, null);

            _tTalentPrefab = typeof(Character).Assembly.GetType("Barotrauma.TalentPrefab", throwOnError: false);
            if (_tTalentPrefab != null)
            {
                _fiTalentPrefabs = _tTalentPrefab.GetField("TalentPrefabs", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var tPrefabBase = _tTalentPrefab.BaseType; // PrefabWithUintIdentifier
                while (tPrefabBase != null && _piUintId == null)
                {
                    _piUintId = tPrefabBase.GetProperty("UintIdentifier", BindingFlags.Public | BindingFlags.Instance);
                    tPrefabBase = tPrefabBase.BaseType;
                }
                var tIdBase = _tTalentPrefab;
                while (tIdBase != null && _piPrefabId == null)
                {
                    _piPrefabId = tIdBase.GetProperty("Identifier", BindingFlags.Public | BindingFlags.Instance);
                    tIdBase = tIdBase.BaseType;
                }
            }
            // _miIsTalentLocked / _uintById resolution failures are NOT fatal:
            // locked-talent check is enforced server-side anyway; the uint map can be
            // retried lazily in ResolveUintIdentifiers.

            return true;
        }

        /// <summary>
        /// Builds the talent Identifier -> UintIdentifier wire map by enumerating the
        /// (internal, build-dependent) TalentPrefab collection. Never uses the internal
        /// type by name at compile time — safe on any build.
        /// </summary>
        private static bool ResolveUintIdentifiers()
        {
            if (_uintById != null && _uintById.Count > 0) { return true; }
            if (_fiTalentPrefabs == null || _piPrefabId == null || _piUintId == null)
            {
                Status = "cannot map talent identifiers (TalentPrefab reflection unavailable in this build)";
                return false;
            }

            _uintById = new Dictionary<Identifier, uint>();
            foreach (var prefab in (System.Collections.IEnumerable)_fiTalentPrefabs.GetValue(null))
            {
                if (prefab == null) { continue; }
                try
                {
                    var id = (Identifier)_piPrefabId.GetValue(prefab);
                    var u = (uint)_piUintId.GetValue(prefab);
                    _uintById[id] = u;
                }
                catch { /* skip malformed entry */ }
            }
            return _uintById.Count > 0;
        }

        // ------------------------------------------------------------- tree mirror

        private sealed class SubTreeCtx
        {
            public Identifier Id;
            public List<List<Identifier>> Stages = new List<List<Identifier>>(); // talent ids per option stage
            public List<int> Required = new List<int>();                          // RequiredTalents per stage
            public List<int> Max = new List<int>();                               // MaxChosenTalents per stage
            public HashSet<Identifier> RequiredTrees = new HashSet<Identifier>();
            public HashSet<Identifier> BlockedTrees = new HashSet<Identifier>();

            public int StageCount(IReadOnlyCollection<Identifier> sel, int stage)
            {
                int n = 0;
                var set = Stages[stage];
                foreach (var id in sel) { if (set.Contains(id)) { n++; } }
                return n;
            }
            public bool AllStagesMaxed(IReadOnlyCollection<Identifier> sel)
            {
                for (int i = 0; i < Stages.Count; i++) { if (StageCount(sel, i) < Max[i]) { return false; } }
                return true;
            }
            public bool AnySelected(IReadOnlyCollection<Identifier> sel)
            {
                for (int i = 0; i < Stages.Count; i++)
                {
                    if (StageCount(sel, i) > 0) { return true; }
                }
                return false;
            }
        }

        private static SubTreeCtx[] _subs;
        private static Dictionary<Identifier, int> _subIndexOf;   // subtree id -> index
        private static Dictionary<Identifier, int> _talentSub;    // talent id -> subtree index

        private static bool BuildTreeMirror(Identifier jobId)
        {
            var jobTreesObj = _fiJobTrees.GetValue(null);
            var tryGetArgs = new object[] { jobId, null };
            if (!(bool)_miTryGet.Invoke(jobTreesObj, tryGetArgs) || tryGetArgs[1] == null)
            {
                return false;
            }
            var tree = tryGetArgs[1];

            _subs = null;
            _subIndexOf = new Dictionary<Identifier, int>();
            _talentSub = new Dictionary<Identifier, int>();

            var rawSubs = new List<object>();
            foreach (var s in (IEnumerable)_fiSubTrees.GetValue(tree)) { rawSubs.Add(s); }

            var list = new List<SubTreeCtx>();
            foreach (var s in rawSubs)
            {
                var ctx = new SubTreeCtx { Id = (Identifier)_piSubId.GetValue(s) };
                foreach (var opt in (IEnumerable)_fiStages.GetValue(s))
                {
                    var ids = new List<Identifier>();
                    foreach (var id in (IEnumerable)_piOptionIds.GetValue(opt)) { ids.Add((Identifier)id); }
                    ctx.Stages.Add(ids);
                    ctx.Required.Add((int)_fiRequiredTalents.GetValue(opt));
                    ctx.Max.Add((int)_fiMaxChosen.GetValue(opt));
                }
                foreach (var id in (IEnumerable)_fiRequiredTrees.GetValue(s)) { ctx.RequiredTrees.Add((Identifier)id); }
                foreach (var id in (IEnumerable)_fiBlockedTrees.GetValue(s)) { ctx.BlockedTrees.Add((Identifier)id); }
                list.Add(ctx);
            }

            _subs = list.ToArray();
            for (int i = 0; i < _subs.Length; i++)
            {
                _subIndexOf[_subs[i].Id] = i;
                foreach (var stage in _subs[i].Stages)
                {
                    foreach (var id in stage) { _talentSub[id] = i; }
                }
            }
            return _subs.Length > 0;
        }

        // ------------------------------------------- structural mirror of the server check
        // Exact replica of TalentTree.IsViableTalentForCharacter MINUS the budget line
        // (budget is enforced separately by the replay simulator, in vanilla order).

        private static bool StructurallyViable(Character c, Identifier t, IReadOnlyCollection<Identifier> sel)
        {
            var info = c?.Info;
            if (info?.Job?.Prefab == null) { return false; }
            // mirror of the server's locked-talent check — via reflection because
            // Character.IsTalentLocked does not exist in every game build.
            // If unavailable: the server-side IsViableTalentForCharacter performs its
            // own check, so the simulator may only overestimate viability; the
            // packet builder's retry/stall logic absorbs that.
            if (_miIsTalentLocked != null &&
                (bool)_miIsTalentLocked.Invoke(c, new object[] { t }))
            {
                return false;
            }
            if (info.GetUnlockedTalentsInTree().Contains(t)) { return true; }   // vanilla early-out

            if (!_talentSub.TryGetValue(t, out int _)) { return false; }

            foreach (var s in _subs)
            {
                if (!s.Stages.Any(stg => stg.Contains(t))) { continue; }

                // vanilla: if the subtree contains t and ALL its stages are maxed -> not viable
                if (s.AllStagesMaxed(sel)) { return false; }

                int ownStage = 0;
                bool found = false;
                for (int i = 0; i < s.Stages.Count; i++)
                {
                    if (s.Stages[i].Contains(t)) { ownStage = i; found = true; break; }
                }
                if (!found) { continue; }

                // vanilla: every stage BEFORE the talent's own stage must be satisfied, else break
                bool blockedByEarlierStage = false;
                for (int i = 0; i < ownStage; i++)
                {
                    if (s.StageCount(sel, i) < s.Required[i]) { blockedByEarlierStage = true; break; }
                }
                if (blockedByEarlierStage) { continue; }    // break -> next subtree (vanilla semantics)

                if (s.StageCount(sel, ownStage) >= s.Max[ownStage]) { return false; }

                // TalentTreeMeetsRequirements
                foreach (var reqId in s.RequiredTrees)
                {
                    if (!_subIndexOf.TryGetValue(reqId, out int ri)) { continue; }
                    var r = _subs[ri];
                    for (int i = 0; i < r.Stages.Count; i++)
                    {
                        if (r.StageCount(sel, i) < r.Required[i]) { return false; }
                    }
                }
                foreach (var blkId in s.BlockedTrees)
                {
                    if (!_subIndexOf.TryGetValue(blkId, out int bi)) { continue; }
                    var b = _subs[bi];
                    if (b.AnySelected(sel) && !b.AllStagesMaxed(sel)) { return false; }
                }
                return true;
            }
            return false;
        }

        // -------------------------------------------- exact server replay (incl. budget)

        private static List<Identifier> SimulateServerPacket(Character c, List<Identifier> packet)
        {
            var selection = new List<Identifier>();
            int budget = c.Info.GetTotalTalentPoints();
            foreach (var t in packet)
            {
                // vanilla order: budget FIRST, then structure (with the unlocked early-out inside)
                if (budget - selection.Count <= 0) { continue; }
                if (StructurallyViable(c, t, selection))
                {
                    selection.Add(t);   // server: GiveTalent + talentSelection.Add
                }
            }
            return selection;
        }

        // ------------------------------------------------------------ packet builder

        private static void AddUnique(List<Identifier> acc, Identifier id)
        {
            if (!acc.Contains(id)) { acc.Add(id); }
        }

        private static void FillStage(List<Identifier> acc, SubTreeCtx s, int stage, HashSet<Identifier> unlocked)
        {
            int need = s.Required[stage];
            int have = acc.Count(id => s.Stages[stage].Contains(id));
            // pass 1: prefer unconfirmed members (they double as unlocks)
            foreach (var id in s.Stages[stage])
            {
                if (have >= need) { return; }
                if (!unlocked.Contains(id) && !acc.Contains(id)) { acc.Add(id); have++; }
            }
            // pass 2: confirmed members as pure support
            foreach (var id in s.Stages[stage])
            {
                if (have >= need) { return; }
                if (!acc.Contains(id)) { acc.Add(id); have++; }
            }
        }

        /// <summary>
        /// Builds the next packet. Returns null when there is nothing left / no progress possible.
        /// </summary>
        private static List<Identifier> BuildPacket(Character c, out int expectedNew)
        {
            var unlocked = c.Info.UnlockedTalents;

            // unconfirmed talents in plan (dependency) order
            var unconfirmed = new List<Identifier>();
            foreach (var id in _plan) { if (!unlocked.Contains(id)) { unconfirmed.Add(id); } }
            expectedNew = 0;
            if (unconfirmed.Count == 0) { return null; }

            var unconfirmedSet = new HashSet<Identifier>(unconfirmed);

            // target subtrees = subtrees containing unconfirmed talents
            var targets = new HashSet<int>();
            foreach (var id in unconfirmed) { targets.Add(_talentSub[id]); }

            // resolve blocker conflicts: if a target subtree blocks another target subtree,
            // defer the blocked one to a later packet (once the blocker is maxed it stops blocking)
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (int si in targets.ToList())
                {
                    foreach (var blkId in _subs[si].BlockedTrees)
                    {
                        if (_subIndexOf.TryGetValue(blkId, out int bi) && bi != si && targets.Contains(bi))
                        {
                            targets.Remove(bi);
                            changed = true;
                        }
                    }
                }
            }

            // subtrees whose talents would BLOCK any of our targets -> excluded from the packet
            var excluded = new HashSet<int>();
            foreach (int si in targets)
            {
                foreach (var blkId in _subs[si].BlockedTrees)
                {
                    if (_subIndexOf.TryGetValue(blkId, out int bi) && !targets.Contains(bi)) { excluded.Add(bi); }
                }
            }

            var packet = new List<Identifier>();

            foreach (int si in targets)
            {
                var s = _subs[si];

                // last stage that still contains an unconfirmed talent
                int lastNew = -1;
                for (int stg = 0; stg < s.Stages.Count; stg++)
                {
                    if (s.Stages[stg].Any(id => unconfirmedSet.Contains(id))) { lastNew = stg; }
                }

                // 1) required trees first: their stages must be satisfied IN-PACKET before t is evaluated
                foreach (var reqId in s.RequiredTrees)
                {
                    if (!_subIndexOf.TryGetValue(reqId, out int ri)) { continue; }
                    if (excluded.Contains(ri)) { continue; }
                    var r = _subs[ri];
                    for (int stg = 0; stg < r.Stages.Count; stg++)
                    {
                        FillStage(packet, r, stg, unlocked);
                    }
                }

                // 2) earlier stages of the own subtree (in-packet tier prerequisites)
                for (int stg = 0; stg < lastNew; stg++)
                {
                    FillStage(packet, s, stg, unlocked);
                }

                // 3) the unconfirmed talents themselves (order = plan order)
                for (int stg = 0; stg <= lastNew; stg++)
                {
                    foreach (var id in s.Stages[stg])
                    {
                        if (unconfirmedSet.Contains(id) && !excluded.Contains(_talentSub[id])) { AddUnique(packet, id); }
                    }
                }
            }

            // keep plan (dependency) order for the wire: it is a valid server replay order
            var ordered = new List<Identifier>();
            foreach (var id in _plan) { if (packet.Contains(id)) { ordered.Add(id); } }

            var given = SimulateServerPacket(c, ordered);
            expectedNew = given.Count(id => unconfirmedSet.Contains(id));
            return ordered;
        }

        // ------------------------------------------------------------------ control

        public static void Start()
        {
            if (Enabled) { return; }
            if (!EnsureReflection()) { DebugConsole.NewMessage("[TalentOverspend] " + Status, Color.Red); return; }

            if (!_patched)
            {
                _harmony = new Harmony("cshub.talentoverspend");
                _harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.ClientEventWrite)),
                    prefix: new HarmonyMethod(typeof(TalentOverspendModule), nameof(ClientEventWritePrefix)));
                _harmony.Patch(AccessTools.Method(typeof(Character), "Update"),
                    postfix: new HarmonyMethod(typeof(TalentOverspendModule), nameof(CharacterUpdatePostfix)));
                _patched = true;
            }

            Enabled = true;
            _phase = Phase.Idle;
            Status = "armed";
            DebugConsole.NewMessage("[TalentOverspend] armed — unlock flood starts once a character is controlled.", Color.Orange);
        }

        public static void Stop(string reason = null)
        {
            if (!Enabled) { return; }
            Enabled = false;
            _phase = Phase.Idle;
            _pendingPayload = null;
            Status = reason ?? "stopped";
            DebugConsole.NewMessage($"[TalentOverspend] stopped: {Status} (packets sent: {_packetsSent})", Color.Orange);
        }

        public static void Toggle()
        {
            if (Enabled) { Stop(); } else { Start(); }
        }

        /// <summary>Optional pump for hubs with their own update loop.</summary>
        public static void Update()
        {
            if (!Enabled) { return; }

            var client = GameMain.Client;
            if (client == null) { Stop("not connected as a client"); return; }

            var c = Character.Controlled;
            if (c?.Info?.Job?.Prefab == null) { return; }          // waiting for a spawned character

            // rebuild the plan if the job changed
            if (_plan == null || _planJobId != c.Info.Job.Prefab.Identifier)
            {
                if (!BuildTreeMirror(c.Info.Job.Prefab.Identifier))
                {
                    Stop("no talent tree for job " + c.Info.Job.Prefab.Identifier);
                    return;
                }
                BuildPlan(c);
                _planJobId = c.Info.Job.Prefab.Identifier;
            }

            double now = Timing.TotalTime;
            switch (_phase)
            {
                case Phase.Idle:
                {
                    _lastUnlockedCount = c.Info.UnlockedTalents.Count;
                    _emptyStreak = 0;
                    SendNext(c, now);
                    break;
                }
                case Phase.WaitEcho:
                {
                    int nowCount = c.Info.UnlockedTalents.Count;
                    if (nowCount > _lastUnlockedCount)
                    {
                        // server echoed -> progress
                        _lastUnlockedCount = nowCount;
                        Status = $"unlocked {_lastUnlockedCount} | {_plan.Count - CountUnconfirmed(c)} left";
                        if (CountUnconfirmed(c) == 0)
                        {
                            Stop($"DONE — full tree unlocked ({_packetsSent} packets)");
                            return;
                        }
                        if (now - _lastSendTime >= SendInterval) { SendNext(c, now); }
                    }
                    else if (now - _lastSendTime > EchoTimeout)
                    {
                        _emptyStreak++;
                        if (_emptyStreak > MaxEmptyStreak)
                        {
                            Stop("stalled (locked talents or structural dead-end)");
                            return;
                        }
                        SendNext(c, now);
                    }
                    break;
                }
            }
        }

        private static int CountUnconfirmed(Character c)
        {
            var unlocked = c.Info.UnlockedTalents;
            int n = 0;
            foreach (var id in _plan) { if (!unlocked.Contains(id)) { n++; } }
            return n;
        }

        private static void BuildPlan(Character c)
        {
            _plan = new List<Identifier>();
            _talentSub.Clear();
            for (int si = 0; si < _subs.Length; si++)
            {
                foreach (var stage in _subs[si].Stages)
                {
                    foreach (var id in stage)
                    {
                        _plan.Add(id);
                        _talentSub[id] = si;
                    }
                }
            }
        }

        private static void SendNext(Character c, double now)
        {
            var packet = BuildPacket(c, out int expectedNew);
            if (packet == null)
            {
                Stop("DONE — nothing left to unlock");
                return;
            }
            if (expectedNew <= 0)
            {
                _emptyStreak++;
                if (_emptyStreak > MaxEmptyStreak)
                {
                    Stop("stalled: the crafted packet unlocks nothing (locked talents? blocker conflict?)");
                    return;
                }
                // try again next tick with the accumulated state
                _phase = Phase.WaitEcho;
                _lastSendTime = now - EchoTimeout + SendInterval;
                return;
            }

            if (!ResolveUintIdentifiers()) { Stop(Status); return; }

            var payload = new List<uint>(packet.Count);
            foreach (var id in packet)
            {
                if (_uintById.TryGetValue(id, out uint uintId)) { payload.Add(uintId); }
            }
            if (payload.Count == 0) { Stop("no valid prefabs in packet"); return; }

            _pendingPayload = payload;                                          // consumed by the write prefix
            GameMain.Client.CreateEntityEvent(c, new Character.UpdateTalentsEventData());
            _packetsSent++;
            _lastSendTime = now;
            _phase = Phase.WaitEcho;
            Status = $"packet #{_packetsSent}: {payload.Count} talents ({expectedNew} new expected)";
            DebugConsole.NewMessage("[TalentOverspend] " + Status, Color.Cyan);
        }

        // ------------------------------------------------------------------ harmony

        // Character.ClientEventWrite prefix: when a payload is queued, write our own
        // UpdateTalents event and skip the vanilla serialization entirely.
        private static bool ClientEventWritePrefix(Character __instance, IWriteMessage msg, NetEntityEvent.IData extraData)
        {
            if (_pendingPayload == null) { return true; }
            if (extraData is not Character.UpdateTalentsEventData) { return true; }

            var payload = _pendingPayload;
            _pendingPayload = null;

            // byte-for-byte vanilla header + body
            msg.WriteRangedInteger(
                (int)((Character.UpdateTalentsEventData)extraData).EventType,
                (int)Character.EventType.MinValue,
                (int)Character.EventType.MaxValue);
            msg.WriteUInt16((ushort)payload.Count);
            foreach (uint uintId in payload) { msg.WriteUInt32(uintId); }
            return false;   // skip original
        }

        // Self-pump: Character.Update runs for every character every frame.
        private static void CharacterUpdatePostfix(Character __instance, float deltaTime, Camera cam)
        {
            if (!Enabled) { return; }
            if (__instance != Character.Controlled) { return; }
            Update();
        }
    }
}
// ============================================================================
//  NOTE: single-target module. It only touches the controlled character's own
//  talent events (CanManageTalents always passes for them). Bot management via
//  this path would additionally require the ManageBotTalents permission.
// ============================================================================
