// ============================================================================
//  TalentOverspendModule.cs — CSHUB drop-in (module + built-in self-test)
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
//      TalentOverspendEngine.Start();        // arm (also self-patches Character.Update)
//      TalentOverspendEngine.Stop();         // disarm
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
    
    // ==========================================================================
    //  GUI entry — appears in the CSHUB menu (derives from CSModuleBase)
    // ==========================================================================
    public class TalentOverspendModule : CSModuleBase
    {
        public override string Id   => "talent_overspend";
        public override string Name => "Talent Overspend";
        public override string Description =>
            "Full talent tree for ~1 real point. The server's UpdateTalents handler budgets against " +
            "GetTotalTalentPoints() (total EARNED, never decremented) minus a packet-local selection " +
            "list that resets on every message => repeat packets unlock everything. Campaign-persistent.";
        public override string Category => "exploit";

        public override string GetLabel() => "Talent Overspend";

        public override void OnClick()
        {
            string status = TalentOverspendEngine.Enabled
                ? $"RUNNING — {TalentOverspendEngine.Status}"
                : "idle";
            if (TalentOverspendEngine.Enabled)
            {
                status += "\n\nUnlocked so far: check console (packet log).";
            }

            var box = new GUIMessageBox(
                "TALENT OVERSPEND EXPLOIT",
                "Server trust flaw: the UpdateTalents handler budgets against a packet-local list, " +
                "so every new packet restarts the budget from your TOTAL earned points.\n" +
                "We flood structurally-valid packets until the whole job tree is unlocked.\n" +
                "Campaign note: UnlockedTalents are saved — this is PERMANENT.\n" +
                "\n" +
                "Current state: " + status + "\n" +
                "\n" +
                "Requirements:\n" +
                " - connected to a multiplayer server as a client\n" +
                " - controlled character with a job (spawned)\n" +
                " - works best with at least 1 unspent point\n" +
                "\n" +
                "Run Self-Test first if unsure (no packets sent).",
                new LocalizedString[]
                {
                    "Start Flood",
                    "Stop",
                    "Self-Test",
                    "Close",
                },
                new Vector2(0.6f, 0.7f));

            box.Buttons[0].Color = Color.LimeGreen;
            box.Buttons[1].Color = Color.OrangeRed;
            box.Buttons[2].Color = Color.CornflowerBlue;
            box.Buttons[3].Color = Color.DarkGray;

            box.Buttons[0].OnClicked = (_, _) =>
            {
                try { TalentOverspendEngine.Start(); }
                catch (Exception e) { DebugConsole.ThrowError("[TalentOverspend] Start failed", e); }
                return true;
            };

            box.Buttons[1].OnClicked = (_, _) =>
            {
                try { TalentOverspendEngine.Stop("stopped from menu"); }
                catch (Exception e) { DebugConsole.ThrowError("[TalentOverspend] Stop failed", e); }
                return true;
            };

            box.Buttons[2].OnClicked = (_, _) =>
            {
                try { TalentOverspendEngine.TalentOverspendSelfTest.Run(); }
                catch (Exception e) { DebugConsole.ThrowError("[TalentOverspend] SelfTest failed", e); }
                return true;
            };

            box.Buttons[3].OnClicked = (_, _) => { box.Close(); return true; };
        }
    }

internal static class TalentOverspendEngine
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

        private const double SendInterval = 0.15;
        private const double EchoTimeout = 6.0;
        private const int MaxEmptyStreak = 50;

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
        private static MemberInfo _piPrefabId;          // Prefab.Identifier — field OR property by build
        private static MemberInfo _piUintId;            // PrefabWithUintIdentifier.UintIdentifier — field OR property
        private static Dictionary<Identifier, uint> _uintById;   // talent id -> wire uint

        internal static bool EnsureReflection()
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

            // AmbiguousMatchException guard: PrefabCollection has TWO TryGet overloads
            // (Identifier and string) — GetMethod with exact types still throws because
            // some builds add more overloads. Pick explicitly by signature.
            foreach (var m in _fiJobTrees.FieldType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != "TryGet") { continue; }
                var ps = m.GetParameters();
                if (ps.Length == 2 &&
                    ps[0].ParameterType == typeof(Identifier) &&
                    ps[1].ParameterType.IsByRef)
                {
                    _miTryGet = m;
                    break;
                }
            }
            if (_miTryGet == null) { Status = "reflection failed: PrefabCollection.TryGet not found"; return false; }

            // ---- optional members (missing in some game builds — degrade gracefully) ----
            foreach (var m in typeof(Character).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (m.Name != "IsTalentLocked") { continue; }
                var ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(Identifier))
                {
                    _miIsTalentLocked = m;
                    break;
                }
            }

            _tTalentPrefab = typeof(Character).Assembly.GetType("Barotrauma.TalentPrefab", throwOnError: false);
            if (_tTalentPrefab != null)
            {
                _fiTalentPrefabs = _tTalentPrefab.GetField("TalentPrefabs", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                // Identifier/UintIdentifier: FIELD in the shipped binary (Prefab.cs:
                // "public readonly Identifier Identifier"), PROPERTY in some builds.
                // Walk the base chain and try both member kinds.
                var t = (Type)_tTalentPrefab;
                while (t != null && _piUintId == null)
                {
                    _piUintId = GetMemberByName(t, "UintIdentifier");
                    t = t.BaseType;
                }
                t = _tTalentPrefab;
                while (t != null && _piPrefabId == null)
                {
                    _piPrefabId = GetMemberByName(t, "Identifier");
                    t = t.BaseType;
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

        /// <summary>Build-dependent member lookup: Prefab.Identifier is a readonly FIELD
        /// in the shipped binary but a PROPERTY in some other builds. Try both.</summary>
        internal static MemberInfo GetMemberByName(Type t, string name)
        {
            return (MemberInfo)t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? (MemberInfo)t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        }

        private static object ReadMember(MemberInfo m, object obj)
            => m switch
            {
                FieldInfo f => f.GetValue(obj),
                PropertyInfo pr => pr.GetValue(obj),
                _ => null
            };

        private static Type MemberType(MemberInfo m)
            => m switch
            {
                FieldInfo f => f.FieldType,
                PropertyInfo pr => pr.PropertyType,
                _ => null
            };

        /// <summary>Reflection-safe talent id -> display name map (TalentPrefab is internal).</summary>
        private static Dictionary<Identifier, string> _nameById;
        private static bool ResolveNames()
        {
            if (_nameById != null) { return true; }
            if (_fiTalentPrefabs == null || _piPrefabId == null) { return false; }
            _nameById = new Dictionary<Identifier, string>();
            foreach (var prefab in (System.Collections.IEnumerable)_fiTalentPrefabs.GetValue(null))
            {
                if (prefab == null) { continue; }
                try
                {
                    var id = (Identifier)ReadMember(_piPrefabId, prefab);
                    string name = id.Value;
                    // try DisplayName then Name via reflection (both optional)
                    var t = prefab.GetType();
                    var dp = t.GetProperty("DisplayName") ?? t.GetProperty("Name");
                    if (dp != null)
                    {
                        var v = dp.GetValue(prefab);
                        if (v is LocalizedString ls && !ls.IsNullOrEmpty()) { name = ls.Value; }
                        else if (v is string s && !string.IsNullOrWhiteSpace(s)) { name = s; }
                    }
                    _nameById[id] = name;
                }
                catch { }
            }
            return _nameById.Count > 0;
        }

        private static string TalentLabel(Identifier id)
            => _nameById != null && _nameById.TryGetValue(id, out var n) ? n : id.Value;

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
                    var id = (Identifier)ReadMember(_piPrefabId, prefab);
                    var u = (uint)ReadMember(_piUintId, prefab);
                    _uintById[id] = u;
                }
                catch { /* skip malformed entry */ }
            }
            return _uintById.Count > 0;
        }

        // ------------------------------------------------------------- tree mirror

        internal sealed class SubTreeCtx
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

        internal static SubTreeCtx[] _subs;
        internal static Identifier _mirrorJobId;   // which job the mirror was built for
        private static Dictionary<Identifier, int> _subIndexOf;   // subtree id -> index
        private static Dictionary<Identifier, int> _talentSub;    // talent id -> subtree index

        internal static bool BuildTreeMirror(Identifier jobId)
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
            _mirrorJobId = jobId;
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

            foreach (var s in TalentOverspendEngine._subs)
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


        /// <summary>
        /// Full server-side viability replica (budget + structure), for the self-test replay.
        /// </summary>
        internal static bool ViableFor(Character c, Identifier t, IReadOnlyCollection<Identifier> sel)
        {
            // vanilla order: budget FIRST
            if (c?.Info == null) { return false; }
            if (c.Info.GetTotalTalentPoints() - sel.Count <= 0) { return false; }
            return StructurallyViable(c, t, sel);
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
            FillStage(acc, s, stage, unlocked, toMax: false);
        }

        private static void FillStage(List<Identifier> acc, SubTreeCtx s, int stage, HashSet<Identifier> unlocked, bool toMax)
        {
            // toMax=false (supports for "requires"/stage-gating): fill to RequiredTalents.
            // toMax=true (maxing the ACTIVE blocker tree): fill to MaxChosenTalents — a
            //   blocking subtree stops blocking only when ALL stages are at Max.
            //
            // PACKET-LOCAL GATING (binary-verified): stage completion AND required-subtree
            // checks evaluate ONLY against the packet's selection list. Already-unlocked
            // talents MUST be re-sent as supports, otherwise a stage-1 talent sees an empty
            // stage-0 count and gets declined ("break" in the vanilla loop). Re-sent
            // unlocked talents DO burn budget slots (they pass via the unlocked early-out
            // and still hit talentSelection.Add), so keep supports at the minimum:
            // Required-fill, never Max-fill, and unlocked members go FIRST (cheapest —
            // they're guaranteed viable via early-out).
            int need = toMax ? s.Max[stage] : s.Required[stage];
            int have = acc.Count(id => s.Stages[stage].Contains(id));

            // pass 1: already-unlocked members first — guaranteed viable via early-out,
            // they anchor the stage count so later new grants can pass gating.
            foreach (var id in s.Stages[stage])
            {
                if (have >= need) { return; }
                if (unlocked.Contains(id) && !acc.Contains(id)) { acc.Add(id); have++; }
            }
            // pass 2: new (unconfirmed) members — real grants
            foreach (var id in s.Stages[stage])
            {
                if (have >= need) { return; }
                if (!unlocked.Contains(id) && !acc.Contains(id)) { acc.Add(id); have++; }
            }
        }

        /// <summary>
        /// Builds the next packet. Returns null when there is nothing left / no progress possible.
        /// </summary>
        private static List<Identifier> BuildPacket(Character c, out int expectedNew)
        {
            var unlocked = c.Info.UnlockedTalents;
            expectedNew = 0;

            // unconfirmed talents in plan (dependency) order; dictionary lookups are
            // defensive — a stale plan/mirror pair must not crash the client.
            var unconfirmed = new List<Identifier>();
            foreach (var id in _plan)
            {
                if (!unlocked.Contains(id) && _talentSub.ContainsKey(id)) { unconfirmed.Add(id); }
            }
            if (unconfirmed.Count == 0) { return null; }

            int active = _talentSub[unconfirmed[0]];
            var activeSub = _subs[active];

            var packet = new List<Identifier>();

            // 1) required-tree supports, filled to REQUIRED only (minimize budget burn).
            //    Already-unlocked members are skipped by FillStage — the server early-out
            //    satisfies those prereqs without re-sending.
            foreach (var reqId in activeSub.RequiredTrees)
            {
                if (!_subIndexOf.TryGetValue(reqId, out int ri)) { continue; }
                if (ri == active) { continue; }
                var r = _subs[ri];
                for (int stg = 0; stg < r.Stages.Count; stg++)
                {
                    FillStage(packet, r, stg, unlocked, toMax: false);
                }
            }

            // 2) earlier stages of the active subtree — also Required-fill (tier prereqs).
            int lastNew = -1;
            for (int stg = 0; stg < activeSub.Stages.Count; stg++)
            {
                if (activeSub.Stages[stg].Any(id => unconfirmed.Contains(id))) { lastNew = stg; }
            }
            for (int stg = 0; stg < lastNew; stg++)
            {
                FillStage(packet, activeSub, stg, unlocked, toMax: false);
            }

            // 3) ONE new talent per packet. Every packet entry (supports included) burns
            //    a budget slot; required/stage-gating supports grow with each tier, so
            //    packing several new talents inflates the floor above the player's points.
            //    Minimal supports + single grant = lowest budget floor per talent.
            var target = unconfirmed[0];
            AddUnique(packet, target);

            // CRITICAL: wire order = packet CONSTRUCTION order (supports already added
            // first). Re-sorting by _plan would put blocker-tree talents BEFORE their
            // required-tree supports, so every support check fails (empty selection at
            // evaluation time) and everything gets declined. Vanilla client sends
            // tree-order too — prereqs always precede dependents.
            var ordered = packet;

            var given = SimulateServerPacket(c, ordered);
            expectedNew = given.Count(unconfirmed.Contains);
            int supports = given.Count(unlocked.Contains);
            if (supports > 0)
            {
                DebugConsole.NewMessage(
                    $"    supports re-sent: {supports} (each burns a budget slot; " +
                    $"packet budget floor ≈ {supports + 1} total points for 1 new grant)",
                    Color.Gray);
            }

            // Detailed dump (user request): every talent in the packet with id + granted/declined.
            DebugConsole.NewMessage($"[TalentOverspend] packet contents ({ordered.Count}):", Color.Cyan);
            foreach (var id in ordered)
            {
                string label = TalentLabel(id);
                string state = given.Contains(id)
                    ? (unlocked.Contains(id) ? "SUPPORT (already unlocked)" : "GRANT")
                    : "declined (structure/budget)";
                Color col = given.Contains(id)
                    ? (unlocked.Contains(id) ? Color.Gray : Color.LightGreen)
                    : Color.Orange;
                DebugConsole.NewMessage($"    [{id.Value}] {label} — {state}", col);
            }

            return ordered;
        }



        // ------------------------------------------------------------------ control

        public static void Start()
        {
            if (Enabled) { return; }
            if (!TalentOverspendEngine.EnsureReflection()) { DebugConsole.NewMessage("[TalentOverspend] " + Status, Color.Red); return; }

            if (!_patched)
            {
                _harmony = new Harmony("cshub.talentoverspend");
                // NOTE: patch helpers live in the ENGINE class, not the GUI module class.
                // nameof() would silently resolve at compile time and crash at runtime
                // if pointed at the wrong type — always qualify explicitly here.
                _harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.ClientEventWrite)),
                    prefix: new HarmonyMethod(typeof(TalentOverspendEngine), nameof(TalentOverspendEngine.ClientEventWritePrefix)));
                _harmony.Patch(AccessTools.Method(typeof(Character), "Update"),
                    postfix: new HarmonyMethod(typeof(TalentOverspendEngine), nameof(TalentOverspendEngine.CharacterUpdatePostfix)));
                _patched = true;
            }

            DumpTreeMap(GameMain.Client != null && Character.Controlled?.Info?.Job?.Prefab != null
                ? Character.Controlled.Info.Job.Prefab.Identifier
                : Identifier.Empty);

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

            // rebuild mirror+plan if the job changed OR the mirror was clobbered
            // (e.g. SelfTest built an engineer mirror while the player runs another job)
            if (_plan == null || _planJobId != c.Info.Job.Prefab.Identifier || _mirrorJobId != c.Info.Job.Prefab.Identifier)
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
                            // Don't stop — cooldown and retry. Server has timing issues.
                            Status = $"cooling down (streak {_emptyStreak})...";
                            _emptyStreak = 20; // partial reset so it retries ~30 more times
                            _lastSendTime = now + 3.0; // 3s cooldown
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

            // Dependency-ordered subtrees: a tree comes after everything it requires.
            // Among simultaneously-ready trees, the one blocking the most others goes
            // first (max it => stop blocking => victims unlock).
            var remaining = Enumerable.Range(0, _subs.Length).ToHashSet();
            var done = new HashSet<int>();
            while (remaining.Count > 0)
            {
                var ready = remaining
                    .Where(si => _subs[si].RequiredTrees
                        .All(rid => !_subIndexOf.ContainsKey(rid) || done.Contains(_subIndexOf[rid])))
                    .ToList();
                if (ready.Count == 0)
                {
                    // circular requires (shouldn't happen) — flush everything
                    ready = remaining.ToList();
                }
                ready.Sort((a, b) =>
                    _subs[b].BlockedTrees.Count(_subIndexOf.ContainsKey)
                        .CompareTo(_subs[a].BlockedTrees.Count(_subIndexOf.ContainsKey)));

                foreach (int si in ready)
                {
                    var s = _subs[si];
                    for (int stg = 0; stg < s.Stages.Count; stg++)
                    {
                        int already = s.Stages[stg].Count(id => c.Info.UnlockedTalents.Contains(id));
                        int budget = Math.Max(0, s.Max[stg] - already);
                        int taken = 0;
                        foreach (var id in s.Stages[stg])
                        {
                            if (taken >= budget) { break; }
                            if (_plan.Contains(id)) { continue; }
                            _plan.Add(id);
                            _talentSub[id] = si;
                            taken++;
                        }
                    }
                    done.Add(si);
                    remaining.Remove(si);
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
                DebugConsole.NewMessage(
                    $"[TalentOverspend] sim says 0 (streak {_emptyStreak}/{MaxEmptyStreak}) — sending anyway",
                    Color.Orange);
                // fall through — send anyway. The sim is a best-effort mirror, not the server.
                // If the server accepts it, the echo will advance the state.
            }

            if (!ResolveUintIdentifiers()) { Stop(Status); return; }
            ResolveNames();

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

        /// <summary>
        /// Prints the full job talent tree: every subtree, every stage with Required/Max,
        /// every talent with its wire id. Also which subtrees require/block which.
        /// </summary>
        private static void DumpTreeMap(Identifier jobId)
        {
            if (jobId.IsEmpty || !BuildTreeMirror(jobId))
            {
                DebugConsole.NewMessage("[TalentOverspend] tree map: (no job yet — will dump on first packet)", Color.Orange);
                return;
            }
            DebugConsole.NewMessage($"[TalentOverspend] TREE MAP for {jobId.Value}:", Color.Cyan);
            for (int si = 0; si < _subs.Length; si++)
            {
                var s = _subs[si];
                string rel = "";
                if (s.RequiredTrees.Any()) { rel += " requires=[" + string.Join(",", s.RequiredTrees.Select(t => t.Value)) + "]"; }
                if (s.BlockedTrees.Any()) { rel += " blocks=[" + string.Join(",", s.BlockedTrees.Select(t => t.Value)) + "]"; }
                DebugConsole.NewMessage($"  subtree [{si}] {s.Id.Value}{rel}", Color.Cyan);
                for (int stg = 0; stg < s.Stages.Count; stg++)
                {
                    DebugConsole.NewMessage(
                        $"    stage {stg} (req {s.Required[stg]}/{s.Max[stg]}):", Color.Cyan);
                    foreach (var id in s.Stages[stg])
                    {
                        string label = TalentLabel(id);
                        uint wire = _uintById != null && _uintById.TryGetValue(id, out var u) ? u : 0;
                        DebugConsole.NewMessage($"      [{id.Value}] {label} (wire: {wire})", Color.Cyan);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ harmony

        // Character.ClientEventWrite prefix: when a payload is queued, write our own
        // UpdateTalents event and skip the vanilla serialization entirely.
        public static bool ClientEventWritePrefix(Character __instance, IWriteMessage msg, NetEntityEvent.IData extraData)
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
        public static void CharacterUpdatePostfix(Character __instance, float deltaTime, Camera cam)
        {
            if (!Enabled) { return; }
            if (__instance != Character.Controlled) { return; }
            Update();
        }

    // ==========================================================================
    //  Nested diagnostics: TalentOverspendModule.SelfTest.Run()
    //  Same 4 invariants as the repo xUnit test, but pure runtime — no packets.
    //  Expected on the buggy build: T1 PASS, T2 PASS, T3 FAIL (overspend), T4 PASS.
    // ==========================================================================
    internal static class TalentOverspendSelfTest
    {
        private static int _pass, _fail;

        private static void Check(bool cond, string name, string detail = "")
        {
            if (cond) { _pass++; DebugConsole.NewMessage($"  [PASS] {name}", Color.LightGreen); }
            else { _fail++; DebugConsole.NewMessage($"  [FAIL] {name} {detail}", Color.Red); }
        }

        private static bool EnvReady()
            => GameMain.NetworkMember != null && GameMain.World != null;

        private static Character CreateChar(int points)
        {
            JobPrefab job = null;
            try { job = JobPrefab.Get("engineer".ToIdentifier()); } catch { }
            job ??= JobPrefab.Prefabs.FirstOrDefault(j => !j.HiddenJob);

            var info = new CharacterInfo(CharacterPrefab.HumanSpeciesName, jobOrJobPrefab: job)
            {
                AdditionalTalentPoints = points
            };
            Vector2 pos = Submarine.MainSub?.WorldPosition ?? Vector2.Zero;
            return Character.Create(
                CharacterPrefab.HumanSpeciesName, pos, ToolBox.RandomSeed(8),
                info, hasAi: false, createNetworkEvent: false);
        }

        private static bool Viable(Character c, Identifier t, IReadOnlyCollection<Identifier> sel)
            => TalentOverspendEngine.ViableFor(c, t, sel);

        private static List<Identifier> PickEntries(Character c, int count)
        {
            var res = new List<Identifier>();
            if (!TalentOverspendEngine.BuildTreeMirror(c.Info.Job.Prefab.Identifier)) { return res; }
            var empty = new List<Identifier>();
            foreach (var s in TalentOverspendEngine._subs)
            {
                foreach (var stage in s.Stages)
                {
                    foreach (var id in stage)
                    {
                        if (res.Contains(id)) { continue; }
                        if (Viable(c, id, empty)) { res.Add(id); if (res.Count >= count) { return res; } }
                    }
                }
            }
            return res;
        }

        private static int Replay(Character c, List<List<Identifier>> packets)
        {
            int granted = 0;
            foreach (var pkt in packets)
            {
                var sel = new List<Identifier>();            // fresh per message — the bug
                foreach (var t in pkt)
                {
                    if (Viable(c, t, sel)) { c.GiveTalent(t, addingFirstTime: false); sel.Add(t); granted++; }
                }
            }
            return granted;
        }

        /// <summary>Run all 4 invariants locally. No packets sent.</summary>
        public static void Run()
        {
            if (!TalentOverspendEngine.EnsureReflection())
            {
                DebugConsole.NewMessage("[SelfTest] reflection unavailable in this build", Color.Orange);
                return;
            }
            if (!EnvReady())
            {
                DebugConsole.NewMessage("[SelfTest] requires a running game session with a level", Color.Orange);
                return;
            }

            _pass = _fail = 0;
            DebugConsole.NewMessage("=== TalentOverspendModule.SelfTest ===", Color.Cyan);
            Character c = null;
            try
            {
                // T1: single batch cap (PASS even on buggy build)
                c = CreateChar(1);
                var two = PickEntries(c, 2);
                if (two.Count >= 2)
                {
                    int avail = c.Info.GetAvailableTalentPoints();
                    var sel = new List<Identifier>(); int g = 0;
                    foreach (var t in two) { if (Viable(c, t, sel)) { c.GiveTalent(t, false); sel.Add(t); g++; } }
                    Check(g <= avail, "T1 single-batch cap", $"granted={g} avail={avail}");
                    Check(c.Info.GetAvailableTalentPoints() == avail - g, "T1 available delta");
                }
                c.Remove(); c = null;

                // T2: one stuffed packet capped (PASS even on buggy build)
                c = CreateChar(1);
                var five = PickEntries(c, 5);
                if (five.Count >= 2)
                {
                    int avail = c.Info.GetAvailableTalentPoints();
                    int g = Replay(c, new List<List<Identifier>> { five });
                    Check(g <= avail, "T2 single-packet bulk cap", $"granted={g} avail={avail}");
                }
                c.Remove(); c = null;

                // T3: cross-packet overspend — THE regression (FAIL on buggy build)
                c = CreateChar(1);
                two = PickEntries(c, 2);
                if (two.Count >= 2)
                {
                    int avail = c.Info.GetAvailableTalentPoints();
                    int g = Replay(c, new List<List<Identifier>>
                    {
                        new List<Identifier> { two[0] },
                        new List<Identifier> { two[1] }
                    });
                    Check(g <= avail, "T3 multi-packet invariant (THE bug)",
                        g > avail ? $"OVERSPEND granted={g} avail={avail}" : "");
                    if (g > avail)
                    {
                        DebugConsole.NewMessage("  >>> CONFIRMED: packet-local budget reset per message.", Color.Yellow);
                    }
                }
                c.Remove(); c = null;

                // T4: root-cause canary (PASS)
                c = CreateChar(1);
                var one = PickEntries(c, 1);
                if (one.Count >= 1)
                {
                    int total = c.Info.GetTotalTalentPoints();
                    int avail = c.Info.GetAvailableTalentPoints();
                    c.GiveTalent(one[0], false);
                    Check(c.Info.GetTotalTalentPoints() == total, "T4 total = quota (never decremented)");
                    Check(c.Info.GetAvailableTalentPoints() == avail - 1, "T4 available reflects spending");
                }
                c.Remove(); c = null;
            }
            catch (Exception e)
            {
                DebugConsole.NewMessage($"[ABORT] {e.GetType().Name}: {e.Message}", Color.Red);
                try { c?.Remove(); } catch { }
            }
            finally
            {
                // SelfTest's BuildTreeMirror clobbers the engine mirror with the TEST
                // job's data. Restore the controlled character's job so the next
                // Start Flood doesn't look up player talents in the wrong dictionary.
                try
                {
                    var player = Character.Controlled;
                    if (player?.Info?.Job?.Prefab != null)
                    {
                        BuildTreeMirror(player.Info.Job.Prefab.Identifier);
                    }
                }
                catch { /* best effort; Update() also re-checks via _mirrorJobId */ }
            }

            DebugConsole.NewMessage(
                $"=== SelfTest done: {_pass} pass, {_fail} fail " +
                (_fail > 0 ? "=> build EXPLOITABLE (only T3 failing is the real bug; T1/T2/T4 pass by design)" : "=> all invariants hold") + " ===",
                _fail > 0 ? Color.Orange : Color.LightGreen);
        }
    }

    }
}
// ============================================================================
//  NOTE: single-target module. It only touches the controlled character's own
//  talent events (CanManageTalents always passes for them). Bot management via
//  this path would additionally require the ManageBotTalents permission.
// ============================================================================
