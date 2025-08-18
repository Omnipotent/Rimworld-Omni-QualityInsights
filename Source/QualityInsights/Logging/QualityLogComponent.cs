using System.Collections.Generic;
using Verse;

namespace QualityInsights.Logging
{
    public class QualityLogComponent : GameComponent
    {
        public List<QualityLogEntry> entries = new();

        public QualityLogComponent(Game game) { }

        public override void ExposeData() =>
            Scribe_Collections.Look(ref entries, "qi_entries", LookMode.Deep);

        public void Add(QualityLogEntry e)
        {
            entries.Add(e);
            if (entries.Count > 5000) entries.RemoveAt(0);
        }

        // NEW: safe accessor for old saves
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
