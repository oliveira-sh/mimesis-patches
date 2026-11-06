// MIMESIS Hallucinations - startup and the Patches window page
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// Above the host's radiation threshold (50 %), a living player can be sent hallucinations by dead teammates: while
// spectating them, E (Interact) asks the host, which checks the rules and tells that player's game to show one: a fake
// mimic only they see, placed outside their view, which comes at them with a shotgun, a bomb or a melee weapon and
// vanishes the moment its attack lands. Nothing about it exists on the server: no damage, no radiation, nothing for
// anyone else. Possession stays as in the game (the dead player only speaks through the mimic), but while a possessor
// talks, living players near the mimic gain radiation (1 %/s, never past 90 %). Everyone needs the patch: the host
// checks and relays, the dead player sends, the living player sees.
//   Shared.cs          constants, wire protocol, logging, runtime facts (the contract between the sides)
//   Settings.cs        host settings, published in the Steam lobby data
//   HostPatches.cs     host side: the game hooks (H1-H5)
//   HostLogic.cs       host side: hello, request validation, relay, cooldowns, cleanup
//   TalkRadiation.cs   host side: radiation from dead players talking through a possessed mimic
//   ClientNet.cs       every player: the per-frame driver, the inbox, hello/ack
//   SpectatorInput.cs  dead player: E (possess or hallucinate) and the spectator HUD tip
//   Victim.cs          living player: shows the hallucinations sent to them, reports how they end
//   FakeMimic.cs       the fake attacker itself (puppet, weapon, movement, attack, vanish), on the target's game and
//                      on the dead players' games
//   Spectate.cs        dead players: seeing the hallucinations, and steering the one you sent
//   Proxy.cs           host: playing a fake player's side (testing with FakePlayers, Debug setting)
//   Placement.cs       living player: where to put it (outside the view)
//   DebugKeys.cs       testing keys (Debug setting)

using System;
using HarmonyLib;
using MimesisPatches;

namespace Hallucinations
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
                // core class turns its part off (vanilla there) instead of leaving half a feature.
                var harmony = new HarmonyLib.Harmony(Const.HarmonyId);
                bool host = true, talk = true, client = true, spectator = true;
                int failed = 0;
                foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
                {
                    try { harmony.CreateClassProcessor(type).Patch(); }
                    catch (Exception e)
                    {
                        failed++;
                        var core = (CoreAttribute)Attribute.GetCustomAttribute(type, typeof(CoreAttribute));
                        if (core != null)
                        {
                            switch (core.Part)
                            {
                                case Part.Host: host = false; break;
                                case Part.Talk: talk = false; break;
                                case Part.Client: client = false; break;
                                case Part.Spectator: spectator = false; break;
                            }
                        }
                        Log.Error($"{type.Name} not applied{(core != null ? $" (core, {core.Part})" : "")}: {e}");
                    }
                }

                // Every player's per-frame driver (hello, HUD, hallucinations, debug keys).
                try { ClientNet.Install(); }
                catch (Exception e) { client = false; Log.Error("Player side not started: " + e); }

                Control.HostReady = host;
                Control.TalkReady = talk;
                Control.ClientReady = client;
                Control.SpectatorReady = client && spectator;
                if (!host) Log.Error("Hosting stays vanilla: a core host patch failed (see above), so nobody gets hallucinations in your games.");
                if (!talk) Log.Error("Talk radiation is off in your games: its patch failed (see above).");
                if (!client) Log.Error("Hallucinations are off on this game: a core patch failed (see above). You won't see or send any.");
                else if (!spectator) Log.Error("Sending hallucinations is off on this game: its patch failed (see above). E stays vanilla possession.");

                CheckRetiredMimicControl();

                PatchMenu.Register("Hallucinations", version, status: Status, build: (page, font) =>
                {
                    PatchUi.Section(page, font, "When you host");
                    PatchUi.OnOff(page, font, "Hallucinations", () => Settings.Enabled, v => Settings.Enabled = v);
                    PatchUi.Slider(page, font, "Radiation above", Settings.ThresholdMin, Settings.ThresholdMax,
                        () => Settings.ThresholdPct, v => Settings.ThresholdPct = v, "%", "Who can be sent hallucinations.");
                    PatchUi.Slider(page, font, "Cooldown", Settings.CooldownMin, Settings.CooldownMax,
                        () => Settings.CooldownSec, v => Settings.CooldownSec = v, " s", "Per dead player, after each one.");
                    PatchUi.Slider(page, font, "Lifetime", Settings.LifetimeMin, Settings.LifetimeMax,
                        () => Settings.LifetimeSec, v => Settings.LifetimeSec = v, " s", "If its attack never lands.");
                    PatchUi.OnOff(page, font, "Shotgun", () => Settings.Shotgun, v => Settings.Shotgun = v);
                    PatchUi.OnOff(page, font, "Bomb", () => Settings.Bomb, v => Settings.Bomb = v);
                    PatchUi.OnOff(page, font, "Melee weapons", () => Settings.Melee, v => Settings.Melee = v);
                    PatchUi.Label(page, font,
                        "One hallucination at a time per player. With every weapon off they come bare-handed (the mimic's grab).",
                        16f, PatchUi.Dim);

                    PatchUi.Section(page, font, "Talking through a mimic (when you host)");
                    PatchUi.Slider(page, font, "Radiation", Settings.TalkMin, Settings.TalkMax,
                        () => Settings.TalkPctPerSec, v => Settings.TalkPctPerSec = v, " %/s", "While the possessor talks. 0 = off.");
                    PatchUi.Slider(page, font, "Range", Settings.RangeMin, Settings.RangeMax,
                        () => Settings.TalkRangeM, v => Settings.TalkRangeM = v, " m", "From the possessed mimic.");
                    PatchUi.Slider(page, font, "Never past", Settings.CapMin, Settings.CapMax,
                        () => Settings.TalkCapPct, v => Settings.TalkCapPct = v, "%", "So talking never kills.");

                    PatchUi.Section(page, font, "How it works");
                    PatchUi.Label(page, font,
                        "Dead: watch a player whose radiation is above the threshold and press E / LT to send them a hallucination: " +
                        "a fake mimic only they (and the dead) see, outside their view, with a shotgun, a bomb or a melee weapon. " +
                        "You steer it: WASD moves, Shift runs, left click attacks (once: it vanishes with its attack), E lets go " +
                        "(it carries on by itself). It does no damage. When a mimic can be possessed, E possesses it as in the " +
                        "game; the tip on your HUD says which one E does.\n" +
                        "While a dead player talks through a possessed mimic, living players within range of it, on the same side " +
                        "(indoors or outdoors), gain radiation.\n" +
                        "Everyone needs this patch: the host checks and relays, the dead player sends, the living player sees. " +
                        "Players without it are never sent one.",
                        18f, PatchUi.Dim);

                    PatchUi.Section(page, font, "Testing");
                    PatchUi.OnOff(page, font, "Debug keys", () => Settings.Debug, v => Settings.Debug = v);
                    PatchUi.Label(page, font,
                        "Ctrl+Shift+H: a hallucination on yourself while alive (any radiation, no cooldown; each press uses the next weapon). " +
                        "Ctrl+Shift+J: +10% radiation to yourself. The host needs Debug keys on too (playing alone, that's you).\n" +
                        "When you host, fake players (FakePlayers patch) can be sent hallucinations too: your game plays their side. " +
                        "Raise one's radiation, die, watch it and press E to steer a hallucination at it without a second player.",
                        16f, PatchUi.Dim);

                    PatchUi.Section(page, font, "Bug reports");
                    PatchUi.OnOff(page, font, "Detailed log", () => Settings.VerboseLog, v => Settings.VerboseLog = v);
                });
                Log.Info($"v{version.ToString(3)}: patches applied" + (failed > 0 ? $", {failed} failed (see errors above)" : "") +
                         $"; host {(host ? "ready" : "OFF")}, talk radiation {(talk ? "ready" : "OFF")}, " +
                         $"player {(client ? "ready" : "OFF")}, sending {(client && spectator ? "ready" : "OFF")}");
            }
            catch (Exception e)
            {
                Log.Error("Failed to apply patches: " + e);
            }
        }

        /// <summary>MimicControl (retired, never released) took possessions over; possession is vanilla again here. Its DLL
        /// can still be in Mods from a test build: say so loudly (the Linux tool's install and uninstall delete it).</summary>
        static void CheckRetiredMimicControl()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "MimicControlRuntime") continue;
                    Control.MimicControlLoaded = true;
                    Log.Error("MimicControlRuntime.dll is still in the game's Mods folder. MimicControl is retired (Hallucinations " +
                              "replaces it) and still changes possession: delete MimicControlRuntime.dll from Mods.");
                    return;
                }
            }
            catch (Exception e) { Log.WarnOnce("mimiccontrol-check", "Couldn't check for MimicControl: " + e.Message); }
        }

        static string Status()
        {
            if (Control.MimicControlLoaded) return "Delete MimicControlRuntime.dll from Mods (retired, still changes possession)";
            if (!Control.HostReady && !Control.ClientReady) return "Not working with this game version (see the log)";
            if (!Settings.Enabled) return "Off when you host";
            var talk = Settings.TalkActive
                ? $"talking +{Settings.TalkPctPerSec}%/s within {Settings.TalkRangeM} m (max {Settings.TalkCapPct}%)"
                : "no talk radiation";
            return $"Above {Settings.ThresholdPct}% radiation, {Settings.CooldownSec} s cooldown; {talk}" + (Settings.Debug ? "; debug keys on" : "");
        }
    }
}
