using System;
using System.Collections.Generic;
using System.Text;
using System.Numerics;
using System.Linq;
using GLFW;
using SharpVk;
using Pengu.Support;
using System.Diagnostics;

namespace Pengu.Renderer
{
    partial class VulkanContext
    {
        public abstract class GameSurface : RenderableModule
        {
            protected VulkanContext Context { get; private set; }

            public List<RenderableModule> Modules { get; private set; } = new List<RenderableModule>();

            public GameSurface(VulkanContext context) => Context = context;

            static readonly TimeSpan fpsMeasurementInterval = TimeSpan.FromSeconds(1);
            int framesRendered;
            TimeSpan totalElapsed;
            protected double FPS { get; private set; }

            public override void UpdateLogic(TimeSpan elapsedTime)
            {
                ++framesRendered;
                totalElapsed += elapsedTime;
                if (totalElapsed >= fpsMeasurementInterval)
                {
                    FPS = framesRendered / (totalElapsed - fpsMeasurementInterval + fpsMeasurementInterval).TotalSeconds;

                    framesRendered = 0;
                    totalElapsed -= fpsMeasurementInterval;
                }
            }
        }
    }
}
