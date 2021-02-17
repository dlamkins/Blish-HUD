using Microsoft.Xna.Framework;

namespace Blish_HUD {
    /// <summary>
    /// Indicates that this can be updated as part of the standard update loop.
    /// </summary>
    public interface IUpdatable {

        void Update(GameTime gameTime);

    }
}