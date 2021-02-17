using Blish_HUD.Render;
using MonoGame.Extended;

namespace Blish_HUD.Entities {

    public interface IEntity : IUpdate, IRenderable3D {

        /// <summary>
        /// Used to order the entities when drawing (to allow for proper blending).
        /// In most instances, this should be the distance from the <see cref="ICamera"/> squared.
        /// See example below. <code>Vector3.DistanceSquared(entity.Position, cameraPosition)</code>
        /// </summary>
        float DrawOrder { get; }

    }

}
