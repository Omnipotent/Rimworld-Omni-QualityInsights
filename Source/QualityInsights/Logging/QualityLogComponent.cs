// Source\QualityInsights\Logging\QualityLogComponent.cs
using System.Collections.Generic;
using System.Linq;
using UnityEngine;            // Time.unscaledDeltaTime
using Verse;
using QualityInsights;       // QualityInsightsMod.Settings

namespace QualityInsights.Logging
{
    public class QualityLogComponent : GameComponent
    {
        public List<QualityLogEntry> entries = new();

        // Accumulated real seconds of play while unpaused (persisted)
        private double playSecondsAccum = 0.0;
        public  double PlaySecondsAccum => playSecondsAccum;

        public QualityLogComponent(Game game) { }

        private const int TicksPerDay = 60000;

        public override void ExposeData()
        {
            // Keep original key for backward compatibility
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                Scribe_Collections.Look(ref entries, "qi_entries", LookMode.Deep);
            }
            else
            {
                Scribe_Collections.Look(ref entries, "qi_entries", LookMode.Deep);
                if (entries == null)
                    Scribe_Collections.Look(ref entries, nameof(entries), LookMode.Deep);
            }
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
                PruneIfNeeded();

            // Persist RL accumulator so deltas survive reloads
            Scribe_Values.Look(ref playSecondsAccum, "qi_playSecondsAccum", 0.0);
        }

        private int _nextMaintenanceTick;
        public override void GameComponentTick()
        {
            int now = Find.TickManager.TicksGame;
            if (now >= _nextMaintenanceTick)
            {
                _nextMaintenanceTick = now + TicksPerDay; // once per in-game day
                PruneIfNeeded();
            }
        }

        // Accumulate *real* play time while in Playing state and unpaused.
        public override void GameComponentUpdate()
        {
            try
            {
                if (Current.Game == null) return;
                if (Current.ProgramState != ProgramState.Playing) return;

                var tm = Find.TickManager;
                if (tm == null || tm.Paused) return;

                // Real seconds, unaffected by game speed (1x/2x/3x)
                playSecondsAccum += Time.unscaledDeltaTime;
            }
            catch
            {
                // Never break the game
            }
        }

        public void Add(QualityLogEntry e)
        {
            // Stamp the entry with current unpaused real-time seconds
            e.playSecondsAtLog = playSecondsAccum;

            entries.Add(e);

            // Soft guard to avoid runaway growth between daily passes
            var s = QualityInsightsMod.Settings;
            if (s.pruneByCount && s.maxEntries > 0 && entries.Count > s.maxEntries * 2)
                PruneIfNeeded();
        }

        public void PruneIfNeeded()
        {
            var s = QualityInsightsMod.Settings;
            int now = Find.TickManager.TicksGame;

            // AGE
            if (s.pruneByAge && s.keepDays > 0)
            {
                int cutoff = now - s.keepDays * TicksPerDay;
                entries.RemoveAll(e => e.gameTicks < cutoff);
            }

            // COUNT (remove oldest first)
            if (s.pruneByCount && s.maxEntries > 0 && entries.Count > s.maxEntries)
            {
                entries.Sort((a, b) => a.gameTicks.CompareTo(b.gameTicks));
                entries.RemoveRange(0, entries.Count - s.maxEntries);
            }
        }

        // Safe accessor for old saves
        public static QualityLogComponent Ensure(Game game)
        {
            var comp = game.GetComponent<QualityLogComponent>();
            if (comp == null)
            {
                comp = new QualityLogComponent(game);
                game.components.Add(comp);
            }
            return comp;
        }
    }
}
