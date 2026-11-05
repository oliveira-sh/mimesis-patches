// MIMESIS MimicControl - startup and the Patches window page
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// In the game a dead player can possess a mimic for 12 s: their voice comes out of it, but its AI keeps moving it.
// With this patch the dead player drives the mimic: walk, run, look, and grab living players, until the mimic dies
// (no time limit, no cooldown), within the host's rules. Only the host and the possessing player need it; everyone
// else sees a normal mimic walking and grabbing. The wire protocol is described at the top of Shared.cs.
//   Shared.cs        constants, wire protocol, logging (the contract between the two sides)
//   Settings.cs      host settings, published in the Steam lobby data
//   HostPatches.cs   host side: takes over the mimic, moves it, grabs, enforces the rules
//   ClientControl.cs possessor side: camera, input, hints

using System;
using HarmonyLib;
using MimesisPatches;

namespace MimicControl
{
    public static class Plugin
    {
        static bool _initialized;

        // Called once at the start of Hub.Awake (see Common/MelonEntry.cs).
        public static void Init()
        {
            if (_initialized) return;
            _initialized = true;
            var version = typeof(Plugin).Assembly.GetName().Version;
            try
            {
                Settings.Load();

                // One class at a time: PatchAll stops at the first target a game update broke and skips the rest. A failed
                // core class turns its side off (vanilla) instead of leaving half a feature that could freeze a mimic.
                var harmony = new HarmonyLib.Harmony(Const.HarmonyId);
                bool hostOk = true, clientOk = true;
                int failed = 0;
                foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
                {
                    try { harmony.CreateClassProcessor(type).Patch(); }
                    catch (Exception e)
                    {
                        failed++;
                        var core = (CoreAttribute)Attribute.GetCustomAttribute(type, typeof(CoreAttribute));
                        if (core != null && core.Side == Side.Host) hostOk = false;
                        if (core != null && core.Side == Side.Client) clientOk = false;
                        Log.Error($"{type.Name} not applied{(core != null ? $" (core, {core.Side} side)" : "")}: {e}");
                    }
                }

                // The possessor side's per-frame driver. It stays idle unless the host supports MimicControl.
                try { ClientControl.Install(); }
                catch (Exception e) { clientOk = false; Log.Error("Possessor side not started: " + e); }

                Control.HostReady = hostOk;
                Control.ClientReady = clientOk;
                if (!hostOk) Log.Error("Hosting stays vanilla: a core host patch failed (see above), so players can't control mimics in your games.");
                if (!clientOk) Log.Error("Controlling mimics is off on this game: a core patch failed (see above); possession works as in the game.");

                PatchMenu.Register("MimicControl", version, status: Status, build: (page, font) =>
                {
                    PatchUi.Section(page, font, "When you host");
                    PatchUi.OnOff(page, font, "Mimic control", () => Settings.Enabled, v => Settings.Enabled = v);
                    PatchUi.Slider(page, font, "Attack cooldown", Settings.CooldownMin, Settings.CooldownMax,
                        () => Settings.AttackCooldownSec, v => Settings.AttackCooldownSec = v, " s", "After a grab ends, before the next.");
                    PatchUi.Slider(page, font, "Attack strength", Settings.StrengthMin, Settings.StrengthMax,
                        () => Settings.AttackStrengthPct, v => Settings.AttackStrengthPct = v, "%", "Contamination a grab adds (100% = a normal mimic).");
                    PatchUi.OnOff(page, font, "One per death", () => Settings.OnePerDeath, v => Settings.OnePerDeath = v);
                    PatchUi.Slider(page, font, "Delay after death", Settings.DelayMin, Settings.DelayMax,
                        () => Settings.DeathDelaySec, v => Settings.DeathDelaySec = v, " s", "Before a dead player can take a mimic.");
                    PatchUi.OnOff(page, font, "Block self-kills", () => Settings.BlockSelfInflicted, v => Settings.BlockSelfInflicted = v);
                    PatchUi.Label(page, font,
                        "One per death: once their mimic dies or they let it go, a player waits until they die again (a revive or the next level resets it).\n" +
                        "Block self-kills: no mimic after dying from a fall, a pit, your own explosive, or contamination no monster or player caused.\n" +
                        "Players who join mid-level never get a mimic. Control only works in levels, never in the tram.",
                        16f, PatchUi.Dim);

                    PatchUi.Section(page, font, "Controls (dead)");
                    PatchUi.Label(page, font,
                        "Take a mimic near the player you watch with E / LT, as in the game. Then: move W A S D / left stick, run Shift / LB, " +
                        "look mouse / right stick, grab left click / RB, let go E / LT.\n" +
                        "The host and the player who takes the mimic need this patch. Everyone else sees a normal mimic.",
                        18f, PatchUi.Dim);

                    PatchUi.Section(page, font, "Bug reports");
                    PatchUi.OnOff(page, font, "Detailed log", () => Settings.VerboseLog, v => Settings.VerboseLog = v);
                });
                Log.Info($"v{version.ToString(3)}: patches applied" + (failed > 0 ? $", {failed} failed (see errors above)" : "") +
                         $"; host side {(hostOk ? "ready" : "OFF")}, possessor side {(clientOk ? "ready" : "OFF")}");
            }
            catch (Exception e)
            {
                Log.Error("Failed to apply patches: " + e);
            }
        }

        static string Status()
        {
            if (!Control.HostReady && !Control.ClientReady) return "Not working with this game version (see the log)";
            if (!Settings.Enabled) return "Off when you host";
            return $"Dead players control mimics: {Settings.AttackCooldownSec} s between grabs, {Settings.AttackStrengthPct}% grab strength" +
                   (Settings.OnePerDeath ? ", one per death" : "");
        }
    }
}
