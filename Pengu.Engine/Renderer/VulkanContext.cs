using SharpVk;
using GLFW;
using SharpVk.Khronos;
using SharpVk.Multivendor;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SixLabors.ImageSharp.PixelFormats;
using System.Numerics;
using System.Collections.Specialized;
using static MoreLinq.Extensions.ForEachExtension;

using Image = SharpVk.Image;
using Buffer = SharpVk.Buffer;
using Version = SharpVk.Version;
using Constants = SharpVk.Constants;
using Exception = System.Exception;
using Pengu.Support;

namespace Pengu.Renderer
{
    public partial class VulkanContext : IDisposable
    {
        readonly NativeWindow window;
        readonly Instance instance;
        readonly DebugReportCallback? debugReportCallback;

        public Extent2D Extent { get; private set; }
        Surface surface;
        PhysicalDevice physicalDevice;
        Device device;
        Queue graphicsQueue, presentQueue, transferQueue;
        QueueFamilyIndices queueIndices;
        CommandPool graphicsCommandPool, presentCommandPool, transientTransferCommandPool;
        Swapchain swapChain;
        Image[] swapChainImages;
        ImageView[] swapChainImageViews;
        Framebuffer[] swapChainFramebuffers;
        RenderPass renderPass;

        CommandBuffer[] swapChainImageCommandBuffers;
        BitVector32 swapChainImageCommandBuffersDirty;

        GameSurface gameSurface;

        Semaphore[] imageAvailableSemaphores, renderingFinishedSemaphores;
        Fence[] inflightFences, imagesInFlight;

        readonly List<CommandBuffer> TransientCommandBuffers = new List<CommandBuffer>();

        interface IInputAction { }

        struct KeyAction : IInputAction
        {
            public Keys Key;
            public int ScanCode;
            public InputState Action;
            public ModifierKeys Modifiers;
        }

        struct CharacterAction : IInputAction
        {
            public string Character;
            public ModifierKeys Modifiers;
        }

        struct MouseMoveAction : IInputAction
        {
            public double X, Y;
        }

        struct MouseButtonAction : IInputAction
        {
            public InputState Action;
            public MouseButton Button;
            public ModifierKeys Modifiers;
        }

        readonly Queue<IInputAction> InputActionQueue = new Queue<IInputAction>();

        // needed for Glfw's events
        static VulkanContext? ContextInstance;

        private struct QueueFamilyIndices
        {
            public uint? GraphicsFamily;
            public uint? TransferFamily;
            public uint? PresentFamily;

            public IEnumerable<uint> Indices
            {
                get
                {
                    if (GraphicsFamily.HasValue)
                        yield return GraphicsFamily.Value;

                    if (PresentFamily.HasValue && PresentFamily != GraphicsFamily)
                        yield return PresentFamily.Value;

                    if (TransferFamily.HasValue && TransferFamily != GraphicsFamily && TransferFamily != PresentFamily)
                        yield return TransferFamily.Value;
                }
            }

            public bool IsComplete => GraphicsFamily.HasValue && PresentFamily.HasValue && TransferFamily.HasValue;

            public static QueueFamilyIndices Find(PhysicalDevice device, Surface surface)
            {
                QueueFamilyIndices indices = default;

                var queueFamilies = device.GetQueueFamilyProperties();

                for (uint index = 0; index < queueFamilies.Length && !indices.IsComplete; index++)
                {
                    if (queueFamilies[index].QueueFlags.HasFlag(QueueFlags.Graphics))
                        indices.GraphicsFamily = index;

                    if (queueFamilies[index].QueueFlags.HasFlag(QueueFlags.Transfer))
                        indices.TransferFamily = index;

                    if (device.GetSurfaceSupport(index, surface))
                        indices.PresentFamily = index;
                }

                indices.TransferFamily ??= indices.GraphicsFamily;

                return indices;
            }
        }

        public VulkanContext(Func<VulkanContext, GameSurface> gameSurfaceGenerator, bool debug)
        {
            if (gameSurfaceGenerator is null)
                throw new ArgumentNullException(nameof(gameSurfaceGenerator));

            ContextInstance = this;

            const int Width = 1280;
            const int Height = 720;

            Glfw.WindowHint(Hint.ClientApi, ClientApi.None);        // Vulkan API
            Glfw.Init();

            window = new NativeWindow(Width, Height, "Pengu");
            Glfw.SetInputMode(window, InputMode.LockKeyMods, 1);

            static void KeyActionCallback(object? sender, KeyEventArgs args) => ContextInstance?.InputActionQueue.Enqueue(
                new KeyAction { Key = args.Key, ScanCode = args.ScanCode, Action = args.State, Modifiers = args.Modifiers });
            window.KeyAction += KeyActionCallback;

            static void CharacterInputCallback(object? sender, CharEventArgs args) => ContextInstance?.InputActionQueue.Enqueue(
                new CharacterAction { Character = args.Char, Modifiers = args.ModifierKeys });
            window.CharacterInput += CharacterInputCallback;

            static void MouseMovedCallback(object? sender, MouseMoveEventArgs args) => ContextInstance?.InputActionQueue.Enqueue(
                new MouseMoveAction { X = args.X / ContextInstance.Extent.Width, Y = args.Y / ContextInstance.Extent.Height });
            window.MouseMoved += MouseMovedCallback;

            static void MouseButtonCallback(object? sender, MouseButtonEventArgs args) => ContextInstance?.InputActionQueue.Enqueue(
                new MouseButtonAction { Action = args.Action, Button = args.Button, Modifiers = args.Modifiers });
            window.MouseButton += MouseButtonCallback;

            const string StandardValidationLayerName = "VK_LAYER_LUNARG_standard_validation";

            // create instance
            string[] enabledLayers;
            string[] enabledExtensions;
            if (debug)
            {
                var availableLayers = Instance.EnumerateLayerProperties();
                enabledLayers = availableLayers.Any(w => w.LayerName == StandardValidationLayerName) ? new[] { StandardValidationLayerName } : Array.Empty<string>();
                enabledExtensions = Vulkan.GetRequiredInstanceExtensions().Append(ExtExtensions.DebugReport).ToArray();
            }
            else
            {
                enabledLayers = Array.Empty<string>();
                enabledExtensions = Vulkan.GetRequiredInstanceExtensions();
            }

            instance = Instance.Create(enabledLayers, enabledExtensions,
                applicationInfo: new ApplicationInfo
                {
                    ApplicationName = "Pengu",
                    ApplicationVersion = new Version(1, 0, 0),
                    EngineName = "SharpVk",
                    EngineVersion = new Version(1, 0, 0),
                    ApiVersion = new Version(1, 1, 0),
                });

            // debug layer
            if (debug)
                debugReportCallback = instance.CreateDebugReportCallback(
                    (flags, objectType, @object, location, messageCode, layerPrefix, message, userData) =>
                    {
                        Debug.WriteLine($"[{flags}][{layerPrefix}] {message}");
                        return flags.HasFlag(DebugReportFlags.Error);
                    }, DebugReportFlags.Error | DebugReportFlags.Warning | DebugReportFlags.PerformanceWarning);

            // create the surface surface
            _ = Vulkan.CreateWindowSurface(new IntPtr((long)instance.RawHandle.ToUInt64()), window.Handle, IntPtr.Zero, out var surfacePtr);
            surface = Surface.CreateFromHandle(instance, (ulong)surfacePtr.ToInt64());

            (physicalDevice, queueIndices) = instance.EnumeratePhysicalDevices()
                .Select(device => (device, q: QueueFamilyIndices.Find(device, surface)))
                .Where(w => w.device.EnumerateDeviceExtensionProperties(null)
                    .Any(extension => extension.ExtensionName == KhrExtensions.Swapchain) && w.q.IsComplete)
                .First();
            var indices = queueIndices.Indices.ToArray();

            // create the logical device
            device = physicalDevice.CreateDevice(
                indices.Select(idx => new DeviceQueueCreateInfo { QueueFamilyIndex = idx, QueuePriorities = new[] { 1f } }).ToArray(),
                null, KhrExtensions.Swapchain);

            // get the queue and create the pool
            graphicsQueue = device.GetQueue(queueIndices.GraphicsFamily!.Value, 0);
            graphicsCommandPool = device.CreateCommandPool(queueIndices.GraphicsFamily!.Value);
            presentQueue = device.GetQueue(queueIndices.GraphicsFamily.Value, 0);
            presentCommandPool = device.CreateCommandPool(queueIndices.PresentFamily!.Value);
            transferQueue = device.GetQueue(queueIndices.TransferFamily!.Value, 0);
            transientTransferCommandPool = device.CreateCommandPool(queueIndices.TransferFamily.Value, CommandPoolCreateFlags.Transient);

            var surfaceCapabilities = physicalDevice.GetSurfaceCapabilities(surface);
            var surfaceFormats = physicalDevice.GetSurfaceFormats(surface);
            var surfacePresentModes = physicalDevice.GetSurfacePresentModes(surface);

            // try to get an R8G8B8_A8_SRGB surface format, otherwise pick the first one in the list of available formats
            var surfaceFormat = surfaceFormats.FirstOrDefault(f => f.ColorSpace == ColorSpace.SrgbNonlinear && f.Format == Format.B8G8R8A8Srgb);
            if (surfaceFormat.Format == Format.Undefined) surfaceFormat = surfaceFormats[0];

            // try to get a mailbox present mode if available, otherwise revert to FIFO which is always available
            var surfacePresentMode = surfacePresentModes.Contains(PresentMode.Mailbox) ? PresentMode.Mailbox : PresentMode.Fifo;

            // construct the swap chain extent based on window size
            if (surfaceCapabilities.CurrentExtent.Width != int.MaxValue)
                Extent = surfaceCapabilities.CurrentExtent;
            else
                Extent = new Extent2D(
                    Math.Max(surfaceCapabilities.MinImageExtent.Width, Math.Min(surfaceCapabilities.MaxImageExtent.Width, Width)),
                    Math.Max(surfaceCapabilities.MinImageExtent.Height, Math.Min(surfaceCapabilities.MaxImageExtent.Height, Height)));

            // swap chain count, has to be between min+1 and max
            var swapChainImageCount = surfaceCapabilities.MinImageCount + 1;
            if (surfaceCapabilities.MaxImageCount > 0 && surfaceCapabilities.MaxImageCount < swapChainImageCount)
                swapChainImageCount = surfaceCapabilities.MaxImageCount;

            imageAvailableSemaphores = Enumerable.Range(0, (int)swapChainImageCount).Select(_ => device.CreateSemaphore()).ToArray();
            renderingFinishedSemaphores = Enumerable.Range(0, (int)swapChainImageCount).Select(_ => device.CreateSemaphore()).ToArray();
            inflightFences = Enumerable.Range(0, (int)swapChainImageCount).Select(_ => device.CreateFence(FenceCreateFlags.Signaled)).ToArray();
            imagesInFlight = new Fence[swapChainImageCount];

            // build the swap chain
            swapChain = device.CreateSwapchain(
                surface, swapChainImageCount, surfaceFormat.Format, surfaceFormat.ColorSpace, Extent, 1, ImageUsageFlags.ColorAttachment,
                indices.Length == 1 ? SharingMode.Exclusive : SharingMode.Concurrent, indices,
                surfaceCapabilities.CurrentTransform, CompositeAlphaFlags.Opaque, surfacePresentMode, true, swapChain);

            // get the swap chain images, and build image views for them
            swapChainImages = swapChain.GetImages();
            swapChainImageViews = swapChainImages
                .Select(i => device.CreateImageView(i, ImageViewType.ImageView2d, surfaceFormat.Format, ComponentMapping.Identity,
                    new ImageSubresourceRange(ImageAspectFlags.Color, 0, 1, 0, 1))).ToArray();

            // the render pass
            renderPass = device.CreateRenderPass(
                new AttachmentDescription
                {
                    Format = surfaceFormat.Format,
                    Samples = SampleCountFlags.SampleCount1,
                    LoadOp = AttachmentLoadOp.Clear,
                    StoreOp = AttachmentStoreOp.Store,
                    StencilLoadOp = AttachmentLoadOp.DontCare,
                    StencilStoreOp = AttachmentStoreOp.DontCare,
                    InitialLayout = ImageLayout.Undefined,
                    FinalLayout = ImageLayout.PresentSource,
                },
                new SubpassDescription
                {
                    PipelineBindPoint = PipelineBindPoint.Graphics,
                    ColorAttachments = new[] { new AttachmentReference { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal } },
                },
                new[]
                {
                    new SubpassDependency
                    {
                        SourceSubpass = Constants.SubpassExternal,
                        DestinationSubpass = 0,
                        SourceStageMask = PipelineStageFlags.BottomOfPipe,
                        SourceAccessMask = AccessFlags.MemoryRead,
                        DestinationStageMask = PipelineStageFlags.ColorAttachmentOutput,
                        DestinationAccessMask = AccessFlags.ColorAttachmentRead | AccessFlags.ColorAttachmentWrite
                    },
                    new SubpassDependency
                    {
                        SourceSubpass = 0,
                        DestinationSubpass = Constants.SubpassExternal,
                        SourceStageMask = PipelineStageFlags.ColorAttachmentOutput,
                        SourceAccessMask = AccessFlags.ColorAttachmentRead | AccessFlags.ColorAttachmentWrite,
                        DestinationStageMask = PipelineStageFlags.BottomOfPipe,
                        DestinationAccessMask = AccessFlags.MemoryRead
                    }
                });

            // and the frame buffers for the render pass
            swapChainFramebuffers = swapChainImageViews
                .Select(iv => device.CreateFramebuffer(renderPass, iv, Extent.Width, Extent.Height, 1))
                .ToArray();

            swapChainImageCommandBuffers = device.AllocateCommandBuffers(graphicsCommandPool, CommandBufferLevel.Primary, (uint)swapChainFramebuffers.Length);

            gameSurface = gameSurfaceGenerator(this);
        }

        ShaderModule CreateShaderModule(string filePath)
        {
            var fileBytes = File.ReadAllBytes(Path.Combine("Shaders", filePath));
            var shaderData = new uint[fileBytes.Length.CeilingIntegerDivide(4)];

            System.Buffer.BlockCopy(fileBytes, 0, shaderData, 0, fileBytes.Length);

            return device.CreateShaderModule(fileBytes.Length, shaderData);
        }

        uint FindMemoryType(uint typeFilter, MemoryPropertyFlags memoryPropertyFlags)
        {
            var memoryProperties = physicalDevice.GetMemoryProperties();

            for (int i = 0; i < memoryProperties.MemoryTypes.Length; i++)
                if ((typeFilter & (1u << i)) > 0 && memoryProperties.MemoryTypes[i].PropertyFlags.HasFlag(memoryPropertyFlags))
                    return (uint)i;

            throw new Exception("No compatible memory type.");
        }

        Buffer CreateBuffer(ulong size, BufferUsageFlags usageFlags, MemoryPropertyFlags memoryPropertyFlags, out DeviceMemory deviceMemory)
        {
            var buffer = device.CreateBuffer(size, usageFlags, SharingMode.Exclusive, null);
            var memRequirements = buffer.GetMemoryRequirements();
            deviceMemory = device.AllocateMemory(memRequirements.Size, FindMemoryType(memRequirements.MemoryTypeBits, memoryPropertyFlags));
            buffer.BindMemory(deviceMemory, 0);

            return buffer;
        }

        CommandBuffer CopyBuffer(Buffer sourceBuffer, Buffer destinationBuffer, ulong size) =>
            RunTransientCommands(commandBuffer =>
            {
                commandBuffer.CopyBuffer(sourceBuffer, destinationBuffer, new BufferCopy { Size = size });
                commandBuffer.PipelineBarrier(PipelineStageFlags.Transfer, PipelineStageFlags.VertexInput, null,
                    new BufferMemoryBarrier
                    {
                        SourceAccessMask = AccessFlags.MemoryWrite,
                        DestinationAccessMask = AccessFlags.VertexAttributeRead,
                        SourceQueueFamilyIndex = uint.MaxValue,
                        DestinationQueueFamilyIndex = uint.MaxValue,
                        Buffer = destinationBuffer,
                        Size = size,
                    }, null);
            });

        CommandBuffer RunTransientCommands(Action<CommandBuffer> action)
        {
            var commandBuffer = device.AllocateCommandBuffer(transientTransferCommandPool, CommandBufferLevel.Primary);
            commandBuffer.Begin(CommandBufferUsageFlags.OneTimeSubmit);
            action(commandBuffer);
            commandBuffer.End();

            TransientCommandBuffers.Add(commandBuffer);

            return commandBuffer;
        }

        CommandBuffer TransitionImageLayout(Image image, ImageLayout oldLayout, ImageLayout newLayout) =>
            RunTransientCommands(commandBuffer =>
            {
                var barrier = new ImageMemoryBarrier
                {
                    OldLayout = oldLayout,
                    NewLayout = newLayout,
                    DestinationQueueFamilyIndex = uint.MaxValue,
                    SourceQueueFamilyIndex = uint.MaxValue,
                    Image = image,
                    SubresourceRange = new ImageSubresourceRange
                    {
                        AspectMask = ImageAspectFlags.Color,
                        LayerCount = 1,
                        LevelCount = 1,
                    },
                };
                PipelineStageFlags sourceStage, destinationStage;

                if (oldLayout == ImageLayout.Undefined && newLayout == ImageLayout.TransferDestinationOptimal)
                {
                    // transfer writes that don't need to wait on anything
                    barrier.SourceAccessMask = 0;
                    barrier.DestinationAccessMask = AccessFlags.TransferWrite;

                    sourceStage = PipelineStageFlags.TopOfPipe;
                    destinationStage = PipelineStageFlags.Transfer;
                }
                else if (oldLayout == ImageLayout.TransferDestinationOptimal && newLayout == ImageLayout.ShaderReadOnlyOptimal)
                {
                    // shader reads should wait on transfer writes, specifically the shader reads in the fragment shader
                    // because that's where we're going to use the texture
                    barrier.SourceAccessMask = AccessFlags.TransferWrite;
                    barrier.DestinationAccessMask = AccessFlags.ShaderRead;

                    sourceStage = PipelineStageFlags.Transfer;
                    destinationStage = PipelineStageFlags.FragmentShader;
                }
                else
                    throw new InvalidOperationException($"Undefined image layout transition from {oldLayout} to {newLayout}");

                commandBuffer.PipelineBarrier(sourceStage, destinationStage, null, null, barrier);
            });

        CommandBuffer CopyBufferToImage2D(Buffer buffer, Image image, uint width, uint height) =>
            RunTransientCommands(
                commandBuffer => commandBuffer.CopyBufferToImage(buffer, image, ImageLayout.TransferDestinationOptimal,
                    new BufferImageCopy
                    {
                        ImageSubresource = new ImageSubresourceLayers
                        {
                            AspectMask = ImageAspectFlags.Color,
                            LayerCount = 1,
                        },
                        ImageOffset = Offset3D.Zero,
                        ImageExtent = new Extent3D(width, height, 1),
                    }));

        Image CreateTextureImage(string fn, out Format format, out DeviceMemory imageMemory)
        {
            Buffer? stagingBuffer = default;
            DeviceMemory? stagingBufferMemory = default;

            // upload to a staging buffer in host memory
            try
            {
                using var imagedata = SixLabors.ImageSharp.Image.Load<Bgra32>(Path.Combine("Media", fn));

                var (width, height) = (imagedata.Width, imagedata.Height);
                var size = width * height * 4;

                if (!imagedata.TryGetSinglePixelSpan(out var pixelSpan))
                    throw new InvalidOperationException($"Could not get pixel span for {fn}");

                stagingBuffer = CreateBuffer((ulong)size, BufferUsageFlags.TransferSource, MemoryPropertyFlags.HostVisible | MemoryPropertyFlags.HostCoherent, out stagingBufferMemory);
                var mappedData = stagingBufferMemory.Map(0, (ulong)size);
                unsafe { pixelSpan.CopyTo(new Span<Bgra32>(mappedData.ToPointer(), size / 4)); }
                stagingBufferMemory.Unmap();

                // create the image
                format = Format.B8G8R8A8Srgb;
                var image = device.CreateImage(ImageType.Image2d, format, new Extent3D((uint)width, (uint)height, 1), 1, 1,
                    SampleCountFlags.SampleCount1, ImageTiling.Optimal, ImageUsageFlags.Sampled | ImageUsageFlags.TransferDestination,
                    SharingMode.Exclusive, queueIndices.TransferFamily, ImageLayout.Undefined);

                // allocate memory for the image 
                var memoryRequirements = image.GetMemoryRequirements();
                imageMemory = device.AllocateMemory(memoryRequirements.Size, FindMemoryType(memoryRequirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocal));
                image.BindMemory(imageMemory, 0);

                // transition into a transfer destination, copy the buffer data, and then transition to shader readonly


                transferQueue.Submit(new SubmitInfo
                {
                    CommandBuffers = new[]
                    {
                        TransitionImageLayout(image, ImageLayout.Undefined, ImageLayout.TransferDestinationOptimal),
                        CopyBufferToImage2D(stagingBuffer, image, (uint)width, (uint)height),
                        TransitionImageLayout(image, ImageLayout.TransferDestinationOptimal, ImageLayout.ShaderReadOnlyOptimal),
                    }
                }, null);
                transferQueue.WaitIdle();

                return image;
            }
            finally
            {
                stagingBuffer?.Dispose();
                stagingBufferMemory?.Free();
            }
        }

        private void UpdateLogic(TimeSpan elapsedTime)
        {
            while (InputActionQueue.Count > 0)
            {
                switch (InputActionQueue.Dequeue())
                {
                    case KeyAction keyAction:
                        gameSurface.ProcessKey(keyAction.Key, keyAction.ScanCode, keyAction.Action, keyAction.Modifiers);
                        break;
                    case MouseMoveAction mouseMoveAction:
                        gameSurface.ProcessMouseMove(mouseMoveAction.X, mouseMoveAction.Y);
                        break;
                    case MouseButtonAction mouseButtonAction:
                        gameSurface.ProcessMouseButton(mouseButtonAction.Button, mouseButtonAction.Action, mouseButtonAction.Modifiers);
                        break;
                    case CharacterAction characterAction:
                        gameSurface.ProcessCharacter(characterAction.Character, characterAction.Modifiers);
                        break;
                    default:
                        throw new InvalidOperationException();
                }
            }

            gameSurface.UpdateLogic(elapsedTime);
        }

        int currentFrame;
        readonly List<CommandBuffer> SubmitCommandBuffers = new List<CommandBuffer>();
        private void DrawFrame()
        {
            device.WaitForFences(inflightFences[currentFrame], true, ulong.MaxValue);

            uint nextImage = swapChain.AcquireNextImage(uint.MaxValue, imageAvailableSemaphores[currentFrame], null);

            if (!(imagesInFlight[nextImage] is null))
                device.WaitForFences(imagesInFlight[nextImage], true, ulong.MaxValue);

            imagesInFlight[nextImage] = inflightFences[currentFrame];

            device.ResetFences(inflightFences[currentFrame]);

            if (gameSurface.Modules.OfType<CommandBufferRenderableModule>().Any(m => m.IsCommandBufferDirty))
            {
                Enumerable.Range(0, swapChainImageCommandBuffers.Length).ForEach(idx => swapChainImageCommandBuffersDirty[1 << idx] = true);
                gameSurface.Modules.OfType<CommandBufferRenderableModule>().ForEach(m => m.IsCommandBufferDirty = false);
            }

            SubmitCommandBuffers.Clear();
            SubmitCommandBuffers.AddRange(gameSurface.PreRender(nextImage));
            foreach (var module in gameSurface.Modules)
                SubmitCommandBuffers.AddRange(module.PreRender(nextImage));

            if (swapChainImageCommandBuffersDirty[1 << (int)nextImage])
            {
                var commandBuffer = swapChainImageCommandBuffers[nextImage];

                commandBuffer.Begin(CommandBufferUsageFlags.SimultaneousUse);
                commandBuffer.BeginRenderPass(renderPass, swapChainFramebuffers[nextImage], new Rect2D(Extent), new ClearValue(), SubpassContents.Inline);
                gameSurface.Modules.OfType<CommandBufferRenderableModule>().ForEach(m => m.Draw(commandBuffer, (int)nextImage));
                commandBuffer.EndRenderPass();
                commandBuffer.End();

                swapChainImageCommandBuffersDirty[1 << (int)nextImage] = false;
            }

            SubmitCommandBuffers.Add(swapChainImageCommandBuffers[nextImage]);

            graphicsQueue.Submit(
                new SubmitInfo
                {
                    CommandBuffers = SubmitCommandBuffers.ToArray(),
                    SignalSemaphores = new[] { renderingFinishedSemaphores[currentFrame] },
                    WaitDestinationStageMask = new[] { PipelineStageFlags.ColorAttachmentOutput },
                    WaitSemaphores = new[] { imageAvailableSemaphores[currentFrame] }
                },
                inflightFences[currentFrame]);

            presentQueue.Present(renderingFinishedSemaphores[currentFrame], swapChain, nextImage);

            currentFrame = (currentFrame + 1) % swapChainImages.Length;

            // free the transient command buffers
            if (TransientCommandBuffers.Any())
            {
                transientTransferCommandPool.FreeCommandBuffers(TransientCommandBuffers.ToArray());
                TransientCommandBuffers.Clear();
            }
        }

        public void Run()
        {
            var sw = Stopwatch.StartNew();
            TimeSpan lastElapsed = default;

            while (!window.IsClosing)
            {
                var totalElapsed = sw.Elapsed;

                UpdateLogic(totalElapsed - lastElapsed);
                DrawFrame();

                Glfw.PollEvents();

                lastElapsed = totalElapsed;
            }
        }

        #region IDisposable Support
        private bool disposedValue; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                }

                gameSurface.Modules.ForEach(m => m.Dispose());
                renderPass.Dispose();
                swapChainImageViews.ForEach(i => i.Dispose());
                swapChain.Dispose();
                imageAvailableSemaphores.ForEach(s => s.Dispose());
                renderingFinishedSemaphores.ForEach(s => s.Dispose());
                inflightFences.ForEach(s => s.Dispose());
                transientTransferCommandPool.Dispose();
                graphicsCommandPool.Dispose();
                presentCommandPool.Dispose();
                device.Dispose();
                surface.Dispose();
                debugReportCallback?.Dispose();
                instance.Dispose();
                window.Dispose();

                disposedValue = true;
            }
        }

        ~VulkanContext()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }

    public abstract class RenderableModule : IDisposable
    {
        public abstract bool ProcessKey(Keys key, int scanCode, InputState action, ModifierKeys modifiers);
        public abstract bool ProcessCharacter(string character, ModifierKeys modifiers);
        public abstract bool ProcessMouseMove(double x, double y);
        public abstract bool ProcessMouseButton(MouseButton button, InputState action, ModifierKeys modifiers);
        public abstract void UpdateLogic(TimeSpan elapsedTime);
        public abstract CommandBuffer[] PreRender(uint nextImage);

        protected abstract void Dispose(bool disposing);

        ~RenderableModule()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: false);
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }

    public abstract class CommandBufferRenderableModule : RenderableModule
    {
        public bool IsCommandBufferDirty { get; set; }
        public abstract void Draw(CommandBuffer commandBuffer, int idx);
    }
}
