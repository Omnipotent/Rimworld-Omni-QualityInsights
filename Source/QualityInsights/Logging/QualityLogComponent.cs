// Source\QualityInsights\Logging\QualityLogComponent.cs
using System.Collections.Generic;
using System.Linq;
using Verse;
using QualityInsights; // for QualityInsightsMod.Settings

namespace QualityInsights.Logging
{
    public class QualityLogComponent : GameComponent
    {
        public List<QualityLogEntry> entries = new();

        public QualityLogComponent(Game game) { }

        private const int TicksPerDay = 60000;

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref entries, "qi_entries", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
                PruneIfNeeded(); // enforce caps after load
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

        public void Add(QualityLogEntry e)
        {
            entries.Add(e);

            // cheap soft guard to avoid runaway growth between daily passes
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
