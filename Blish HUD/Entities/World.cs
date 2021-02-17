using System.Collections.Generic;
using System.Linq;
using Blish_HUD.GameServices.Render;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoGame.Extended;

namespace Blish_HUD.Entities {

    public class World : IRenderable, IUpdate, IWorld {

        public SynchronizedCollection<IEntity> Entities { get; private set; } = new SynchronizedCollection<IEntity>();
        
        private IEnumerable<IEntity> GetEntities(bool sorted = false) {
            lock (this.Entities.SyncRoot) {
                return sorted
                    ? this.Entities.OrderByDescending(e => e.DrawOrder).ToArray()
                    : this.Entities.ToArray();
            }
        }

        public void Update(GameTime gameTime) {
            foreach (var entity in GetEntities()) {
                entity.Update(gameTime);
            }
        }

        public void Render(GraphicsDevice graphicsDevice) {
            graphicsDevice.BlendState        = BlendState.AlphaBlend;
            graphicsDevice.DepthStencilState = DepthStencilState.DepthRead;
            graphicsDevice.SamplerStates[0]  = SamplerState.LinearWrap;
            graphicsDevice.RasterizerState   = RasterizerState.CullNone;

            foreach (var entity in GetEntities(true)) {
                entity.Render(graphicsDevice, this, GameService.Gw2Mumble.PlayerCamera);
            }
        }
    }

}
