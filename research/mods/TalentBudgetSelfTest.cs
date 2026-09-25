// ============================================================================
//  TalentBudgetSelfTest.cs — CSHUB / workshop build (client binary).
//
//  ⚠️ THIS IS NOT THE REPO xUNIT TEST. BarotraumaTest/TalentBudgetTests.cs lives
//  in the BarotraumaTest project (xUnit + FluentAssertions + extern alias) and
//  must NEVER be dropped into a mod's Modules folder. This file reimplements the
//  same invariants as RUNTIME DIAGNOSTICS that compile against the installed
//  game's public API only.
//
//  Constraints honored (each was a compile error in the naive port):
//   - no xunit / FluentAssertions / [Fact] / [Collection]  → manual asserts,
//     report via DebugConsole;
//   - no `extern alias Client`                             → none needed;
//   - client assembly has NO GameMain.Server               → environment check
//     via GameMain.NetworkMember != null (shared property);
//   - Character.Create(CharacterInfo, ...) overload absent → speciesName overload;
//   - TalentTree / TalentSubTree / TalentOption are INTERNAL and the released
//     binary has NO InternalsVisibleTo for mod assemblies → ALL tree access via
//     reflection (same approach as TalentOverspendModule.cs).
//
//  USAGE: TalentBudgetSelfTest.Run();
//  Prints PASS/FAIL per invariant to the local console. Purely local logic
//  replay — sends NO packets, touches NO server.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    public static class TalentBudgetSelfTest
    {
        // ------------------------------------------------------------ reflection

        private static bool _resolved;
        private static bool _resolveOk;

        private static FieldInfo _fiJobTrees;         // static TalentTree.JobTalentTrees
        private static MethodInfo _miTryGet;          // PrefabCollection<TalentTree>.TryGet
        private static MethodInfo _miViable3;         // static IsViableTalentForCharacter(char, Identifier, IReadOnlyCollection<Identifier>)
        private static FieldInfo _fiSubTrees;         // TalentTree.TalentSubTrees
        private static FieldInfo _fiStages;           // TalentSubTree.TalentOptionStages
        private static PropertyInfo _piOptionIds;     // TalentOption.TalentIdentifiers

        private static void Resolve()
        {
            if (_resolved) { return; }
            _resolved = true;

            var asm = typeof(CharacterInfo).Assembly;
            var tTree = asm.GetType("Barotrauma.TalentTree", false);
            var tSub = asm.GetType("Barotrauma.TalentSubTree", false);
            var tOpt = asm.GetType("Barotrauma.TalentOption", false);
            if (tTree == null || tSub == null || tOpt == null) { return; }

            _fiJobTrees = tTree.GetField("JobTalentTrees", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            _fiSubTrees = tTree.GetField("TalentSubTrees", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _fiStages = tSub.GetField("TalentOptionStages", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _piOptionIds = tOpt.GetProperty("TalentIdentifiers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _miViable3 = tTree.GetMethod("IsViableTalentForCharacter",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(Character), typeof(Identifier), typeof(IReadOnlyCollection<Identifier>) }, null);
            if (_fiJobTrees == null) { return; }
            _miTryGet = _fiJobTrees.FieldType.GetMethod("TryGet", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            _resolveOk = _fiSubTrees != null && _fiStages != null && _piOptionIds != null &&
                         _miViable3 != null && _miTryGet != null;
        }

        private static bool Viable(Character c, Identifier t, IReadOnlyCollection<Identifier> sel)
            => (bool)_miViable3.Invoke(null, new object[] { c, t, sel });

        // ------------------------------------------------------------- environment

        /// <summary>Shared-only checks; the client binary has no GameMain.Server.</summary>
        private static bool EnvironmentReady()
            => GameMain.NetworkMember != null && GameMain.World != null;

        private static Character CreateTestCharacter(int talentPoints)
        {
            JobPrefab job = null;
            try { job = JobPrefab.Get("engineer".ToIdentifier()); } catch { /* fallback below */ }
            job ??= JobPrefab.Prefabs.FirstOrDefault(j => !j.HiddenJob);

            var info = new CharacterInfo(CharacterPrefab.HumanSpeciesName, jobOrJobPrefab: job)
            {
                AdditionalTalentPoints = talentPoints // setter clamps to 0..100
            };

            Vector2 pos = Submarine.MainSub?.WorldPosition ?? Vector2.Zero;
            // speciesName overload — the CharacterInfo-first overload does not exist
            // in every shipped build.
            return Character.Create(
                CharacterPrefab.HumanSpeciesName,
                pos,
                ToolBox.RandomSeed(8),
                info,
                hasAi: false,
                createNetworkEvent: false);
        }

        private static List<Identifier> PickEntryTalents(Character c, int count)
        {
            var result = new List<Identifier>();
            if (!_resolveOk) { return result; }

            var tryArgs = new object[] { c.Info.Job.Prefab.Identifier, null };
            if (!(bool)_miTryGet.Invoke(_fiJobTrees.GetValue(null), tryArgs) || tryArgs[1] == null) { return result; }

            var empty = new List<Identifier>();
            foreach (var sub in (System.Collections.IEnumerable)_fiSubTrees.GetValue(tryArgs[1]))
            {
                foreach (var stage in (System.Collections.IEnumerable)_fiStages.GetValue(sub))
                {
                    foreach (Identifier id in (System.Collections.IEnumerable)_piOptionIds.GetValue(stage))
                    {
                        if (result.Contains(id)) { continue; }
                        if (Viable(c, id, empty))
                        {
                            result.Add(id);
                            if (result.Count >= count) { return result; }
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>Byte-faithful replay of the server's UpdateTalents handler loop.</summary>
        private static int ReplayHandler(Character c, List<List<Identifier>> packets)
        {
            int granted = 0;
            foreach (var packet in packets)
            {
                var talentSelection = new List<Identifier>(); // fresh per network message — the bug
                foreach (var t in packet)
                {
                    if (Viable(c, t, talentSelection))
                    {
                        c.GiveTalent(t, addingFirstTime: false);
                        talentSelection.Add(t);
                        granted++;
                    }
                }
            }
            return granted;
        }

        // ---------------------------------------------------------------- runner

        private static int _pass, _fail;

        private static void Check(bool cond, string name, string detail = "")
        {
            if (cond) { _pass++; DebugConsole.NewMessage($"  [PASS] {name}", Color.LightGreen); }
            else { _fail++; DebugConsole.NewMessage($"  [FAIL] {name} {detail}", Color.Red); }
        }

        /// <summary>Runs all invariants locally. No packets sent.</summary>
        public static void Run()
        {
            Resolve();
            DebugConsole.NewMessage("=== TalentBudgetSelfTest ===", Color.Cyan);

            if (!_resolveOk)
            {
                DebugConsole.NewMessage("[SKIP] talent tree reflection unavailable in this build", Color.Orange);
                return;
            }
            if (!EnvironmentReady())
            {
                DebugConsole.NewMessage("[SKIP] requires a running game session with a loaded level", Color.Orange);
                return;
            }

            _pass = _fail = 0;
            Character c = null;
            try
            {
                // ---- test 1: single batch holds (expected PASS even on buggy build) ----
                {
                    c = CreateTestCharacter(1);
                    var two = PickEntryTalents(c, 2);
                    if (two.Count >= 2)
                    {
                        int availBefore = c.Info.GetAvailableTalentPoints();
                        var sel = new List<Identifier>();
                        int granted = 0;
                        foreach (var t in two)
                        {
                            if (Viable(c, t, sel)) { c.GiveTalent(t, addingFirstTime: false); sel.Add(t); granted++; }
                        }
                        int availAfter = c.Info.GetAvailableTalentPoints();
                        Check(granted <= availBefore, "T1 single-batch cap",
                            $"granted={granted} avail={availBefore}");
                        Check(availAfter == availBefore - granted, "T1 available delta");
                    }
                    else { DebugConsole.NewMessage("  [SKIP] T1: fewer than 2 entry talents for job", Color.Orange); }
                    c.Remove(); c = null;
                }

                // ---- test 2: one stuffed packet capped (expected PASS) ----
                {
                    c = CreateTestCharacter(1);
                    var five = PickEntryTalents(c, 5);
                    if (five.Count >= 2)
                    {
                        int availBefore = c.Info.GetAvailableTalentPoints();
                        int granted = ReplayHandler(c, new List<List<Identifier>> { five });
                        Check(granted <= availBefore, "T2 single-packet bulk cap",
                            $"granted={granted} avail={availBefore}");
                    }
                    else { DebugConsole.NewMessage("  [SKIP] T2: fewer than 2 entry talents", Color.Orange); }
                    c.Remove(); c = null;
                }

                // ---- test 3: cross-packet overspend (expected FAIL on current build!) ----
                {
                    c = CreateTestCharacter(1);
                    var two = PickEntryTalents(c, 2);
                    if (two.Count >= 2)
                    {
                        int initialAvail = c.Info.GetAvailableTalentPoints();
                        int granted = ReplayHandler(c, new List<List<Identifier>>
                        {
                            new List<Identifier> { two[0] },
                            new List<Identifier> { two[1] }
                        });
                        int availAfter = c.Info.GetAvailableTalentPoints();
                        Check(granted <= initialAvail, "T3 multi-packet invariant (THE bug)",
                            $"OVERSPEND: granted={granted} avail={initialAvail}");
                        Check(availAfter == initialAvail - granted, "T3 cumulative available delta");
                        if (granted > initialAvail)
                        {
                            DebugConsole.NewMessage(
                                "  >>> CONFIRMED: packet-local selection resets budget per message.", Color.Yellow);
                        }
                    }
                    else { DebugConsole.NewMessage("  [SKIP] T3: fewer than 2 entry talents", Color.Orange); }
                    c.Remove(); c = null;
                }

                // ---- test 4: root-cause canary (expected PASS) ----
                {
                    c = CreateTestCharacter(1);
                    var one = PickEntryTalents(c, 1);
                    if (one.Count >= 1)
                    {
                        int totalBefore = c.Info.GetTotalTalentPoints();
                        int availBefore = c.Info.GetAvailableTalentPoints();
                        c.GiveTalent(one[0], addingFirstTime: false);
                        Check(c.Info.GetTotalTalentPoints() == totalBefore,
                            "T4 GetTotalTalentPoints is a quota (never decremented)");
                        Check(c.Info.GetAvailableTalentPoints() == availBefore - 1,
                            "T4 GetAvailableTalentPoints reflects spending");
                        Check(c.Info.GetTotalTalentPoints() - 0 > 0,
                            "T4 handler budget line stays positive after spend (the trap)");
                    }
                    else { DebugConsole.NewMessage("  [SKIP] T4: no entry talent", Color.Orange); }
                    c.Remove(); c = null;
                }
            }
            catch (Exception e)
            {
                DebugConsole.NewMessage($"[ABORT] {e.GetType().Name}: {e.Message}", Color.Red);
                try { c?.Remove(); } catch { /* best effort */ }
            }

            DebugConsole.NewMessage($"=== done: {_pass} pass, {_fail} fail " +
                $"({_fail > 0 ? "server build is EXPLOITABLE (T3)" : "invariants hold"}). " +
                "NOTE: T1/T2/T4 are expected to pass on the buggy build; only T3 is the real regression test. ===",
                _fail > 0 ? Color.Orange : Color.LightGreen);
        }
    }
}
// ============================================================================
//  Expected output on the current (buggy) build:
//    T1 PASS, T2 PASS, T3 FAIL (OVERSPEND granted=2 avail=1), T4 PASS
//    summary line: "server build is EXPLOITABLE (T3)"
//  On a fixed build all four PASS.
// ============================================================================
