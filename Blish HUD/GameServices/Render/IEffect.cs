﻿using System;
using Microsoft.Xna.Framework.Graphics;

namespace Blish_HUD.GameServices.Render {
    public interface IEffect : IDisposable {

        EffectTechnique CurrentTechnique { get; set; }

        EffectParameterCollection Parameters { get; }
        
        EffectTechniqueCollection Techniques { get; }

    }
}
