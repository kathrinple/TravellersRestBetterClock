using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace BetterClock
{
    public enum SpeedState { normal, fast, slow }

    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        private static Harmony _harmony;
        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> _debugLogging;
        internal static ConfigEntry<int> _ticksToUpdate;
        internal static ConfigEntry<KeyCode> _speedHotKey;
        internal static ConfigEntry<KeyCode> _pauseHotKey;
        internal static ConfigEntry<float> _speedMultSlow;
        internal static ConfigEntry<float> _speedMultFast;
        internal static ConfigEntry<bool> _ThreeCharDays;

        public static GameDate betterClockTime;
        public static bool paused = false;
        public static SpeedState gameSpeed = SpeedState.normal;
        internal static string gameSpeedText = "";

        internal static WorldTime _worldTime = null;
        internal static TextMeshProUGUI _clockText = null;
        internal static TimeUI _timeUI = null;

        private static int _tickCount = 0;
        private static int _tickTime = 0;
        private static int _clockTextSearchCooldown = 0;
        private static FieldInfo _currentGameDateField = null;

        public Plugin()
        {
            _debugLogging = Config.Bind("Debug", "Debug Logging", false, "Logs additional information to console");
            _ticksToUpdate = Config.Bind("General", "Ticks Between Updates", 10, "Only update the time data every X frames");
            _speedHotKey = Config.Bind("Speed Control", "hotkey", KeyCode.F9, "Press to toggle between normal/fast/slow speeds");
            _pauseHotKey = Config.Bind("Speed Control", "pause key", KeyCode.None, "Press to toggle between paused/unpaused");
            _speedMultSlow = Config.Bind("Speed Control", "Slow Speed", 0.2f, "Clock speed multiplier in Slow mode");
            _speedMultFast = Config.Bind("Speed Control", "Fast Speed", 5.0f, "Clock speed multiplier in Fast mode");
            _ThreeCharDays = Config.Bind("General", "Three Character Days", false, "Shows 'Wed' instead of 'We' etc.");
        }

        private void Awake()
        {
            Log = Logger;
            _harmony = Harmony.CreateAndPatchAll(typeof(Plugin));
            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
            gameSpeed = SpeedState.normal;
            InjectIntoPlayerLoop();
        }

        private void OnDestroy()
        {
            _harmony.UnpatchSelf();
        }

        internal static void DebugLog(string message)
        {
            if (_debugLogging != null && _debugLogging.Value)
                Log.LogInfo($"BetterClock: {message}");
        }

        internal static void SetWorldSpeed()
        {
            float newSpeed;
            if (paused) { newSpeed = 0.0f; }
            else if (gameSpeed == SpeedState.fast) { newSpeed = _speedMultFast.Value; gameSpeedText = "+"; }
            else if (gameSpeed == SpeedState.slow) { newSpeed = _speedMultSlow.Value; gameSpeedText = "-"; }
            else { newSpeed = 1.0f; gameSpeedText = ""; }
            WorldTime.multiplierDevConsole = newSpeed;
        }

        // WorldTime postfix — grabbed at frame 0 before FishingTweaks removes it.
        // ReadTime() re-reads via Resources.FindObjectsOfTypeAll after that.
        [HarmonyPatch(typeof(WorldTime), "Update")]
        [HarmonyPostfix]
        static void WorldTimeUpdatePostfix(WorldTime __instance)
        {
            _worldTime = __instance;
            if (_currentGameDateField == null)
                _currentGameDateField = typeof(WorldTime).GetField("currentGameDate",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (_currentGameDateField != null)
                betterClockTime = (GameDate)_currentGameDateField.GetValue(__instance);
            else
                betterClockTime = Traverse.Create(__instance).Field("currentGameDate").GetValue<GameDate>();
        }

        // ── PlayerLoop injection ──────────────────────────────────────────────

        static void InjectIntoPlayerLoop()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            bool ok = TryInject(ref loop);
            PlayerLoop.SetPlayerLoop(loop);
            if (!ok) Log.LogError("BetterClock: PlayerLoop injection FAILED — clock will not update");
        }

        static bool TryInject(ref PlayerLoopSystem loop)
        {
            if (loop.subSystemList == null) return false;
            for (int i = 0; i < loop.subSystemList.Length; i++)
            {
                if (loop.subSystemList[i].type == typeof(PostLateUpdate))
                {
                    var subs = loop.subSystemList[i].subSystemList ?? new PlayerLoopSystem[0];
                    var newSubs = new PlayerLoopSystem[subs.Length + 1];
                    Array.Copy(subs, newSubs, subs.Length);
                    newSubs[subs.Length] = new PlayerLoopSystem
                    {
                        type = typeof(Plugin),
                        updateDelegate = BetterClockTick
                    };
                    loop.subSystemList[i].subSystemList = newSubs;
                    return true;
                }
                if (TryInject(ref loop.subSystemList[i])) return true;
            }
            return false;
        }

        static void BetterClockTick()
        {
            _tickCount++;
            try
            {
                HandleHotkeys();
                ReadTime();
                UpdateClockText();
            }
            catch (Exception e)
            {
                if (_tickCount <= 10)
                    Log.LogError($"BetterClock: Tick exception #{_tickCount}: {e}");
            }
        }

        // ── Per-frame logic (formerly in ClockHelper) ────────────────────────

        static void HandleHotkeys()
        {
            if (_speedHotKey != null && _speedHotKey.Value != KeyCode.None
                && Input.GetKeyDown(_speedHotKey.Value))
            {
                if (gameSpeed == SpeedState.normal) gameSpeed = SpeedState.fast;
                else if (gameSpeed == SpeedState.fast) gameSpeed = SpeedState.slow;
                else gameSpeed = SpeedState.normal;
                SetWorldSpeed();
            }
            if (_pauseHotKey != null && _pauseHotKey.Value != KeyCode.None
                && Input.GetKeyDown(_pauseHotKey.Value))
            {
                paused = !paused;
                SetWorldSpeed();
            }
        }

        static void ReadTime()
        {
            if (++_tickTime <= (_ticksToUpdate != null ? _ticksToUpdate.Value : 10))
                return;
            _tickTime = 0;

            if (_worldTime == null)
            {
                var all = Resources.FindObjectsOfTypeAll<WorldTime>();
                if (all.Length > 0)
                {
                    _worldTime = all[0];
                    DebugLog($"Found WorldTime active={_worldTime.isActiveAndEnabled}");
                }
            }

            // Read time directly — WorldTimeUpdatePostfix may have been removed by another mod
            if (_worldTime != null)
            {
                if (_currentGameDateField == null)
                    _currentGameDateField = typeof(WorldTime).GetField("currentGameDate",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (_currentGameDateField != null)
                    betterClockTime = (GameDate)_currentGameDateField.GetValue(_worldTime);
                else
                    betterClockTime = Traverse.Create(_worldTime).Field("currentGameDate").GetValue<GameDate>();
            }
        }

        static void EnableAutoSize(TextMeshProUGUI tmp)
        {
            float original = tmp.fontSize;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = 4f;
            tmp.fontSizeMax = (original > 0 ? original : 36f) * 0.88f;
        }

        // TimeUI.Update() postfix — runs after the game's own update so tired/sleep warnings
        // still fire, then we overwrite only the clock text with our custom format.
        [HarmonyPatch(typeof(TimeUI), "Update")]
        [HarmonyPostfix]
        static void TimeUIUpdatePostfix()
        {
            UpdateClockText();
        }

        static void UpdateClockText()
        {
            if (_clockText == null)
            {
                if (_clockTextSearchCooldown > 0) { _clockTextSearchCooldown--; return; }
                _clockTextSearchCooldown = 120; // retry at most once every ~2 seconds

                var allTimeUIs = Resources.FindObjectsOfTypeAll<TimeUI>();
                DebugLog($"FindAll<TimeUI>={allTimeUIs.Length}");
                if (allTimeUIs.Length == 0) return;

                var timeUI = allTimeUIs[0];
                _timeUI = timeUI;

                _clockText = Traverse.Create(timeUI).Field("showingTextMesh")
                    .GetValue<TextMeshProUGUI>();
                if (_clockText != null)
                {
                    EnableAutoSize(_clockText);
                }
                else
                {
                    var fields = typeof(TimeUI).GetFields(
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    foreach (var f in fields)
                    {
                        if (f.FieldType == typeof(TextMeshProUGUI))
                        {
                            var val = (TextMeshProUGUI)f.GetValue(timeUI);
                            if (_clockText == null && val != null)
                                _clockText = val;
                        }
                    }

                    if (_clockText == null)
                    {
                        var tmps = timeUI.GetComponentsInChildren<TextMeshProUGUI>(true);
                        if (tmps.Length > 0)
                            _clockText = tmps[0];
                    }

                    if (_clockText == null)
                    {
                        Log.LogWarning("BetterClock: Could not find clock text in TimeUI");
                        return;
                    }
                    EnableAutoSize(_clockText);
                }
            }

            if (_clockText == null) return;

            string clocktext;
            if (!paused)
            {
                int h = betterClockTime.hour;
                int m = betterClockTime.min - betterClockTime.min % 5;
                int dayLength = _ThreeCharDays != null && _ThreeCharDays.Value ? 3 : 2;
                string weekday = betterClockTime.day.ToString().Substring(0, dayLength);
                int dayofmonth = (int)(betterClockTime.week * GameDate.DAY_IN_WEEK
                                       + betterClockTime.day + 1);
                string hx = h < 10 ? "0" + h : h.ToString();
                string mx = m < 10 ? "0" + m : m.ToString();
                clocktext = $"{hx}:{mx} {weekday} {dayofmonth}{gameSpeedText}";
            }
            else
            {
                clocktext = "PAUSED";
            }

            if (_clockText.text == clocktext) return;
            _clockText.text = clocktext;
            _clockText.maxVisibleCharacters = clocktext.Length;
            DebugLog($"Wrote clock text: {clocktext}");
        }
    }
}
