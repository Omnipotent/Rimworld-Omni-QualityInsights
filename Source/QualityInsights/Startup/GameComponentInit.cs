using Verse;
using QualityInsights.Logging;

namespace QualityInsights.Startup
{
    // Ensures the GameComponent exists in every save/new game.
    [StaticConstructorOnStartup]
    public static class GameComponentInit
    {
        static GameComponentInit()
        {
            if (Current.Game == null) return;
            if (Current.Game.GetComponent<QualityLogComponent>() == null)
            {
                Current.Game.components.Add(new QualityLogComponent(Current.Game));
            }
        }
    }
}
