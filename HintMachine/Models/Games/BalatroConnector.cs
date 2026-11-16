using HintMachine.Models.GenericConnectors;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace HintMachine.Models.Games
{
    /// <summary>
    /// Connector for Balatro. It does not read the game's RAM: instead it relies on the
    /// BalatroHintMachine Steamodded mod, which writes a JSON snapshot of the game state to
    /// %AppData%/Roaming/Balatro/HintMachine/state.json. This connector watches that file and
    /// feeds its quests from it.
    ///
    /// Bridge protocol (state.json), written by the mod:
    /// {
    ///   "protocol": 2,          // protocol version, must match BRIDGE_PROTOCOL_VERSION
    ///   "seq": 1284,            // incremented on every write, used to skip already-seen states
    ///   "checksum": 9182,       // sum of all bytes of the JSON state object without the checksum
    ///                           // field (the bridge mod computes it before appending the field);
    ///                           // used to detect torn reads
    ///   "in_run": true,
    ///   "ante": 3,
    ///   "chips": "125000000",   // sent as a string because chips can exceed 2^53
    ///   "counters": { "boss_blinds_beaten": 5, "runs_won": 1, "hands_played": 42, "jokers_bought": 6 }
    /// }
    /// </summary>
    public class BalatroConnector : IGameConnector
    {
        private const string GAME_PROCESS_NAME = "Balatro";
        private const int BRIDGE_PROTOCOL_VERSION = 2;
        private const double CHIPS_CLAMP = 1e15; // avoid overflow when casting to long
        private const long CHIPS_PER_TICK_CAP = 1000000000000; // cap chips movement per read (anti hint-flood)

        private readonly HintQuestCounter _bossBlindsQuest = new HintQuestCounter {
            Name = "Boss blinds beaten",
            GoalValue = 2,
            AwardedHints = 1,
            CooldownBetweenIncrements = 30,
            Description = "Defeat boss blinds during your runs."
        };

        private readonly HintQuestCounter _runsWonQuest = new HintQuestCounter {
            Name = "Runs won",
            GoalValue = 1,
            AwardedHints = 1,
            CooldownBetweenIncrements = 600,
            Description = "Win a run by beating the ante 8 boss blind."
        };

        private readonly HintQuestCounter _handsPlayedQuest = new HintQuestCounter {
            Name = "Hands played",
            GoalValue = 30,
            AwardedHints = 1,
            Description = "Play poker hands during your runs."
        };

        private readonly HintQuestCounter _jokersBoughtQuest = new HintQuestCounter {
            Name = "Jokers bought",
            GoalValue = 8,
            AwardedHints = 1,
            Description = "Buy jokers from the shop."
        };

        private readonly HintQuestCumulative _anteQuest = new HintQuestCumulative {
            Name = "Ante reached",
            GoalValue = 3,
            AwardedHints = 1,
            Description = "Reach a new ante with your run."
        };

        private readonly HintQuestCumulative _chipsQuest = new HintQuestCumulative {
            Name = "Chips scored",
            GoalValue = 5000000,
            AwardedHints = 1,
            MaxIncrease = long.MaxValue, // a single hand can score huge amounts late-game
            Description = "Score chips with your poker hands."
        };

        // Maps counter names from the bridge state file to their quest
        private readonly Dictionary<string, HintQuestCounter> _counterQuests;

        private FileSystemWatcher _watcher = null;
        private string _stateFilePath = "";
        private volatile bool _stateFileDirty = false;
        private bool _hasReadStateOnce = false;
        private long _lastSeq = -1;
        private long _lastChipsRaw = -1;
        private readonly Dictionary<string, long> _lastCounterValues = new Dictionary<string, long>();

        // ----------------------------------------------------------------------------------

        public BalatroConnector()
        {
            Name = "Balatro";
            Description = "Track your Balatro runs to earn hints. Requires the BalatroHintMachine Steamodded mod " +
                          "(in %AppData%/Roaming/Balatro/Mods), which bridges the game state to HintMachine " +
                          "through %AppData%/Roaming/Balatro/HintMachine/state.json.";
            Platform = "PC";
            SupportedVersions.Add("Steam (Steamodded)");
            CoverFilename = "balatro.png";
            Author = "HommeQuiParleAuxChevres";

            _counterQuests = new Dictionary<string, HintQuestCounter>
            {
                { "boss_blinds_beaten", _bossBlindsQuest },
                { "runs_won", _runsWonQuest },
                { "hands_played", _handsPlayedQuest },
                { "jokers_bought", _jokersBoughtQuest },
            };

            Quests.Add(_bossBlindsQuest);
            Quests.Add(_runsWonQuest);
            Quests.Add(_anteQuest);
            Quests.Add(_handsPlayedQuest);
            Quests.Add(_chipsQuest);
            Quests.Add(_jokersBoughtQuest);
        }

        protected override bool Connect()
        {
            try
            {
                if (!IsBalatroRunning())
                    return false;

                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string bridgeDirectory = Path.Combine(appData, "Balatro", "HintMachine");
                _stateFilePath = Path.Combine(bridgeDirectory, "state.json");

                // The bridge mod must be installed and have written its state file at least once
                if (!File.Exists(_stateFilePath))
                    return false;

                // Setup a watcher to be notified when the bridge state file is changed
                _watcher = new FileSystemWatcher
                {
                    Path = bridgeDirectory,
                    Filter = "state.json",
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };
                _watcher.Changed += new FileSystemEventHandler((object source, FileSystemEventArgs e) => { _stateFileDirty = true; });
                _watcher.Created += new FileSystemEventHandler((object source, FileSystemEventArgs e) => { _stateFileDirty = true; });
                _watcher.Renamed += new RenamedEventHandler((object source, RenamedEventArgs e) => { _stateFileDirty = true; });

                ReadStateFile(); // initial read to sync baselines without awarding past events

                return true;
            }
            catch
            {
                return false;
            }
        }

        public override void Disconnect()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }

        protected override bool Poll()
        {
            // Never let an exception escape: the polling thread only catches ProcessRamWatcherException
            try
            {
                if (!IsBalatroRunning())
                    return false;

                if (_stateFileDirty || !_hasReadStateOnce)
                {
                    _stateFileDirty = false;
                    ReadStateFile();
                }

                return true;
            }
            catch (Exception e)
            {
                Logger.Debug($"BalatroConnector poll error: {e.Message}");
                return true;
            }
        }

        private bool IsBalatroRunning()
        {
            return Process.GetProcessesByName(GAME_PROCESS_NAME).Length > 0;
        }

        private void ReadStateFile()
        {
            try
            {
                if (!File.Exists(_stateFilePath))
                    return;

                byte[] rawBytes;
                using (FileStream fs = new FileStream(_stateFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length == 0)
                        return;

                    rawBytes = new byte[fs.Length];
                    int totalRead = 0;
                    while (totalRead < rawBytes.Length)
                    {
                        int n = fs.Read(rawBytes, totalRead, rawBytes.Length - totalRead);
                        if (n <= 0)
                            return; // torn read, wait for the next write
                        totalRead += n;
                    }
                }

                string raw = Encoding.UTF8.GetString(rawBytes);
                JObject root = JObject.Parse(raw);

                int protocol = root.Value<int?>("protocol") ?? -1;
                if (protocol != BRIDGE_PROTOCOL_VERSION)
                {
                    Logger.Warn($"Balatro bridge protocol mismatch: got {protocol}, expected {BRIDGE_PROTOCOL_VERSION}. " +
                                "Make sure the BalatroHintMachine mod and HintMachine are up to date.");
                    return;
                }

                // The checksum is the sum of all bytes of the state object without the checksum
                // field; the bridge mod computes it on the JSON object before appending the field.
                // Stripping the whole ,"checksum":N segment (separating comma included, with any
                // whitespace inside it) from the file leaves exactly those bytes, making the check
                // insensitive to the segment's formatting.
                long reportedChecksum = root.Value<long?>("checksum") ?? -1;
                Match checksumMatch = Regex.Match(raw, ",\\s*\"checksum\"\\s*:\\s*\\d+");
                if (!checksumMatch.Success || reportedChecksum < 0)
                {
                    Logger.Debug("Balatro bridge state file has no valid checksum, ignoring read.");
                    return;
                }

                long computedChecksum = 0;
                for (int i = 0; i < rawBytes.Length; i++)
                {
                    if (i >= checksumMatch.Index && i < checksumMatch.Index + checksumMatch.Length)
                        continue;
                    computedChecksum += rawBytes[i];
                }
                if (reportedChecksum != computedChecksum)
                {
                    Logger.Debug("Balatro bridge state file checksum mismatch, ignoring torn read.");
                    return;
                }

                long seq = root.Value<long?>("seq") ?? -1;
                if (seq <= _lastSeq)
                    return;
                _lastSeq = seq;
                _hasReadStateOnce = true;

                JObject counters = root["counters"] as JObject;
                if (counters != null)
                {
                    foreach (KeyValuePair<string, HintQuestCounter> kv in _counterQuests)
                    {
                        long value = counters.Value<long?>(kv.Key) ?? 0;
                        if (!_lastCounterValues.ContainsKey(kv.Key))
                        {
                            // First read for this counter: set the baseline without awarding past events
                            _lastCounterValues[kv.Key] = value;
                            continue;
                        }

                        long delta = value - _lastCounterValues[kv.Key];
                        if (delta > 0)
                            kv.Value.CurrentValue += delta;
                        _lastCounterValues[kv.Key] = value;
                    }
                }

                long ante = root.Value<long?>("ante") ?? 0;
                _anteQuest.UpdateValue(ante);

                // Chips are sent as a string by the bridge because they can exceed 2^53
                // Accepting chips = 0 matters: it re-baselines the quest between runs so that
                // the first hand of a new run is credited instead of silently swallowed.
                string chipsStr = root.Value<string>("chips");
                if (chipsStr != null &&
                    double.TryParse(chipsStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double chips) &&
                    chips >= 0)
                {
                    long chipsLong = (long)Math.Min(chips, CHIPS_CLAMP);
                    if (_lastChipsRaw >= 0 && chipsLong > _lastChipsRaw + CHIPS_PER_TICK_CAP)
                        chipsLong = _lastChipsRaw + CHIPS_PER_TICK_CAP;
                    _chipsQuest.UpdateValue(chipsLong);
                    _lastChipsRaw = chipsLong;
                }
            }
            catch (Exception e)
            {
                // Torn / locked / malformed read: ignore this tick, the next write will be clean
                Logger.Debug($"BalatroConnector could not read bridge state file: {e.Message}");
            }
        }
    }
}
