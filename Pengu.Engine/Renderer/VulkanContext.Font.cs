using System;
using System.Collections.Generic;
using System.Text;
using SharpVk;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Linq;
using System.IO;
using System.Net;
using Pengu.Support;
using System.Runtime.CompilerServices;
using GLFW;

using Image = SharpVk.Image;
using Buffer = SharpVk.Buffer;
using MoreLinq;
using SixLabors.ImageSharp;
using SharpVk.Interop.Khronos;
using System.Net.NetworkInformation;
using System.Diagnostics.CodeAnalysis;

namespace Pengu.Renderer
{
    partial class VulkanContext
    {
        public class Font : CommandBufferRenderableModule
        {
            public const char PrintableSpace = ' ';
            readonly VulkanContext context;
            private readonly Dictionary<char, (float u0, float v0, float u1, float v1)> Characters =
                new Dictionary<char, (float u0, float v0, float u1, float v1)>();

            private TimeSpan totalElapsedTime;

            Buffer vertexIndexBuffer, stagingVertexIndexBuffer;
            DeviceMemory vertexIndexBufferMemory, stagingIndexVertexBufferMemory;
            IntPtr stagingIndexVertexBufferMemoryStartPtr;

            struct PerImageResourcesType
            {
                public Buffer uniformBuffer;
                public DeviceMemory uniformBufferMemory;
                public IntPtr uniformBufferMemoryStartPtr;
                public DescriptorSet descriptorSet;
            }

            PerImageResourcesType[] perImageResources;

            PipelineLayout pipelineLayout;
            DescriptorSetLayout descriptorSetLayout;
            Pipeline pipeline;
            Image fontTextureImage;
            DeviceMemory fontTextureImageMemory;
            ImageView fontTextureImageView;
            Sampler fontTextureSampler;

            public uint MaxCharacters { get; private set; } 
            public uint MaxVertices => MaxCharacters * 4;
            public uint MaxIndices => MaxCharacters * 6;

            public uint UsedCharacters { get; private set; }
            public uint UsedVertices => UsedCharacters * 4;
            public uint UsedIndices => UsedCharacters * 6;

            readonly List<FontString> fontStrings = new List<FontString>();

            public bool IsBufferDataDirty { get; set; }

            public Font(VulkanContext context, string fontName, uint maxCharacters = 4000)
            {
                if (string.IsNullOrEmpty(fontName))
                    throw new ArgumentException("message", nameof(fontName));
                this.context = context ?? throw new ArgumentNullException(nameof(context));

                MaxCharacters = maxCharacters;

                using (var binfile = new BinaryReader(File.Open(Path.Combine("Media", fontName + ".bin"), FileMode.Open)))
                {
                    var length = binfile.BaseStream.Length;
                    do
                    {
                        const float offset1 = 0.001f, offset2u = 0, offset2v = 0.00f;
                        Characters.Add(binfile.ReadChar(), (binfile.ReadSingle() + offset1, binfile.ReadSingle() + offset1, binfile.ReadSingle() + offset2u, binfile.ReadSingle() + offset2v));
                    } while (binfile.BaseStream.Position < length);
                }

                CreateVertexIndexBuffers();

                perImageResources = new PerImageResourcesType[context.swapChainImages.Length];

                for (int idx = 0; idx < context.swapChainImages.Length; ++idx)
                {
                    perImageResources[idx].uniformBuffer = context.CreateBuffer(FontUniformObject.Size, BufferUsageFlags.UniformBuffer,
                        MemoryPropertyFlags.HostVisible | MemoryPropertyFlags.HostCoherent, out perImageResources[idx].uniformBufferMemory);
                    perImageResources[idx].uniformBufferMemoryStartPtr = perImageResources[idx].uniformBufferMemory.Map(0, FontUniformObject.Size);
                }

                // build the font texture objects
                fontTextureImage = context.CreateTextureImage("pt_mono.png", out var format, out fontTextureImageMemory);
                fontTextureImageView = context.device.CreateImageView(fontTextureImage, ImageViewType.ImageView2d, format, ComponentMapping.Identity,
                    new ImageSubresourceRange { AspectMask = ImageAspectFlags.Color, LayerCount = 1, LevelCount = 1 });

                fontTextureSampler = context.device.CreateSampler(Filter.Linear, Filter.Linear, SamplerMipmapMode.Linear, SamplerAddressMode.ClampToBorder, SamplerAddressMode.ClampToBorder,
                    SamplerAddressMode.ClampToBorder, 0, false, 1, false, CompareOp.Always, 0, 0, BorderColor.IntOpaqueBlack, false);

                using var vShader = context.CreateShaderModule("font.vert.spv");
                using var fShader = context.CreateShaderModule("font.frag.spv");

                var descriptorPool = context.device.CreateDescriptorPool((uint)context.swapChainImages.Length,
                    new[]
                    {
                        new DescriptorPoolSize
                        {
                            Type = DescriptorType.UniformBuffer,
                            DescriptorCount = (uint)context.swapChainImages.Length,
                        },
                        new DescriptorPoolSize
                        {
                            Type = DescriptorType.CombinedImageSampler,
                            DescriptorCount = (uint)context.swapChainImages.Length
                        }
                    });

                descriptorSetLayout = context.device.CreateDescriptorSetLayout(
                    new[]
                    {
                        new DescriptorSetLayoutBinding
                        {
                            Binding = 0,
                            DescriptorCount = 1,
                            DescriptorType = DescriptorType.UniformBuffer,
                            StageFlags = ShaderStageFlags.Vertex,
                        },
                        new DescriptorSetLayoutBinding
                        {
                            Binding = 1,
                            DescriptorCount = 1,
                            DescriptorType = DescriptorType.CombinedImageSampler,
                            StageFlags = ShaderStageFlags.Fragment,
                        }
                    });

                context.device.AllocateDescriptorSets(descriptorPool, Enumerable.Repeat(descriptorSetLayout, context.swapChainImages.Length).ToArray())
                    .ForEach((ds, idx) => perImageResources[idx].descriptorSet = ds);

                for (int idx = 0; idx < context.swapChainImages.Length; ++idx)
                    context.device.UpdateDescriptorSets(
                        new[]
                        {
                            new WriteDescriptorSet
                            {
                                DestinationSet = perImageResources[idx].descriptorSet,
                                DestinationBinding = 0,
                                DestinationArrayElement = 0,
                                DescriptorType = DescriptorType.UniformBuffer,
                                DescriptorCount = 1,
                                BufferInfo = new[]
                                {
                                    new DescriptorBufferInfo
                                    {
                                        Buffer = perImageResources[idx].uniformBuffer,
                                        Offset = 0,
                                        Range = FontUniformObject.Size
                                    }
                                },
                            },
                            new WriteDescriptorSet
                            {
                                DestinationSet = perImageResources[idx].descriptorSet,
                                DestinationBinding = 1,
                                DestinationArrayElement = 0,
                                DescriptorType = DescriptorType.CombinedImageSampler,
                                DescriptorCount = 1,
                                ImageInfo = new[]
                                {
                                    new DescriptorImageInfo
                                    {
                                        ImageLayout= ImageLayout.ShaderReadOnlyOptimal,
                                        ImageView = fontTextureImageView,
                                        Sampler = fontTextureSampler,
                                    }
                                }
                            }
                        }, null);

                pipelineLayout = context.device.CreatePipelineLayout(descriptorSetLayout, null);

                pipeline = context.device.CreateGraphicsPipeline(null,
                    new[]
                    {
                        new PipelineShaderStageCreateInfo { Stage = ShaderStageFlags.Vertex, Module = vShader, Name = "main" },
                        new PipelineShaderStageCreateInfo { Stage = ShaderStageFlags.Fragment, Module = fShader, Name = "main" },
                    },
                    new PipelineRasterizationStateCreateInfo { LineWidth = 1 },
                    pipelineLayout, context.renderPass, 0, null, -1,
                    vertexInputState: new PipelineVertexInputStateCreateInfo
                    {
                        VertexAttributeDescriptions = FontVertex.AttributeDescriptions,
                        VertexBindingDescriptions = new[] { FontVertex.BindingDescription },
                    },
                    inputAssemblyState: new PipelineInputAssemblyStateCreateInfo { Topology = PrimitiveTopology.TriangleList },
                    viewportState: new PipelineViewportStateCreateInfo
                    {
                        Viewports = new[] { new Viewport(0, 0, context.Extent.Width, context.Extent.Height, 0, 1) },
                        Scissors = new[] { new Rect2D(context.Extent) },
                    },
                    colorBlendState: new PipelineColorBlendStateCreateInfo
                    {
                        Attachments = new[]
                        {
                            new PipelineColorBlendAttachmentState
                            {
                                ColorWriteMask = ColorComponentFlags.R | ColorComponentFlags.G | ColorComponentFlags.B | ColorComponentFlags.A,
                            }
                        }
                    },
                    multisampleState: new PipelineMultisampleStateCreateInfo
                    {
                        SampleShadingEnable = false,
                        RasterizationSamples = SampleCountFlags.SampleCount1,
                        MinSampleShading = 1
                    });
            }

            [MemberNotNull(nameof(vertexIndexBuffer), nameof(vertexIndexBufferMemory), nameof(stagingVertexIndexBuffer), nameof(stagingIndexVertexBufferMemory))]
            private void CreateVertexIndexBuffers()
            {
                var vertexSize = (ulong)(FontVertex.Size * MaxVertices);
                var indexSize = (ulong)(sizeof(ushort) * MaxIndices);

                DisposeVertexIndexBuffers();

                stagingVertexIndexBuffer = context.CreateBuffer(vertexSize + indexSize, BufferUsageFlags.TransferSource,
                    MemoryPropertyFlags.HostVisible | MemoryPropertyFlags.HostCoherent, out stagingIndexVertexBufferMemory);
                stagingIndexVertexBufferMemoryStartPtr = stagingIndexVertexBufferMemory.Map(0, vertexSize + indexSize);

                vertexIndexBuffer = context.CreateBuffer(vertexSize + indexSize, BufferUsageFlags.TransferDestination | BufferUsageFlags.VertexBuffer,
                    MemoryPropertyFlags.DeviceLocal, out vertexIndexBufferMemory);
            }

            private void DisposeVertexIndexBuffers()
            {
                vertexIndexBuffer?.Dispose();
                vertexIndexBufferMemory?.Free();
                stagingVertexIndexBuffer?.Dispose();
                stagingIndexVertexBufferMemory?.Free();
            }

            public override unsafe CommandBuffer[] PreRender(uint nextImage)
            {
                CommandBuffer? resultCommandBuffer = default;

                if (IsBufferDataDirty)
                {
                    UsedCharacters = (uint)fontStrings.Sum(fs =>
                        (fs.Value?.Count(s => s != ' ' && s != '\n' && s != '\b') ?? 0) + (fs.FillBackground ? 1 : 0));

                    if (MaxVertices < UsedVertices)
                    {
                        MaxCharacters = (uint)(UsedCharacters * Math.Max(1.5, (double)UsedVertices / MaxVertices * 1.5));
                        CreateVertexIndexBuffers();
                    }

                    // build the string vertices
                    var vertexPtr = (FontVertex*)stagingIndexVertexBufferMemoryStartPtr.ToPointer();
                    ushort vertexIdx = 0;
                    var indexPtr = (ushort*)(vertexPtr + MaxVertices);

                    foreach (var fs in fontStrings)
                        if (!string.IsNullOrWhiteSpace(fs.Value))
                        {
                            var x = fs.Position.X;
                            var y = fs.Position.Y;

                            // fill background?
                            if (fs.FillBackground)
                            {
                                var (u0, v0, u1, v1) = Characters[' '];
                                var aspect = (u1 - u0) / (v1 - v0);

                                *vertexPtr++ = new FontVertex(new Vector4(x / context.Extent.AspectRatio, y, 0, 0),
                                    fs.DefaultBackground, fs.DefaultForeground, false, fs.Offset);
                                *vertexPtr++ = new FontVertex(new Vector4(x / context.Extent.AspectRatio, y + fs.Size * fs.Height, 0, 0),
                                    fs.DefaultBackground, fs.DefaultForeground, false, fs.Offset);
                                *vertexPtr++ = new FontVertex(new Vector4((x + fs.Size * fs.Width * aspect) / context.Extent.AspectRatio, y, 0, 0),
                                    fs.DefaultBackground, fs.DefaultForeground, false, fs.Offset);
                                *vertexPtr++ = new FontVertex(new Vector4((x + fs.Size * fs.Width * aspect) / context.Extent.AspectRatio, y + fs.Size * fs.Height, 0, 0),
                                    fs.DefaultBackground, fs.DefaultForeground, false, fs.Offset);

                                *indexPtr++ = (ushort)(vertexIdx + 0);
                                *indexPtr++ = (ushort)(vertexIdx + 1);
                                *indexPtr++ = (ushort)(vertexIdx + 2);

                                *indexPtr++ = (ushort)(vertexIdx + 2);
                                *indexPtr++ = (ushort)(vertexIdx + 1);
                                *indexPtr++ = (ushort)(vertexIdx + 3);

                                vertexIdx += 4;
                            }

                            // draw each character
                            int charIndex = 0;

                            foreach (var ch in fs.Value)
                            {
                                if (ch == '\n')
                                {
                                    x = fs.Position.X;
                                    y += fs.Size;
                                }
                                else if (ch == ' ')
                                {
                                    var (u0, v0, u1, v1) = Characters[' '];
                                    x += fs.Size * (u1 - u0) / (v1 - v0);
                                }
                                else if (ch == '\b')
                                {
                                    var (u0, v0, u1, v1) = Characters[' '];
                                    x -= fs.Size * (u1 - u0) / (v1 - v0);
                                }
                                else
                                {
                                    var (u0, v0, u1, v1) = Characters[ch == PrintableSpace ? ' ' : ch];
                                    var aspect = (u1 - u0) / (v1 - v0);
                                    var xSize = fs.Size * aspect;

                                    var @override = fs.TryGetOverrideForIndex(charIndex);

                                    var bg = fs.DefaultBackground;
                                    var fg = fs.DefaultForeground;
                                    var selected = false;

                                    if (@override.HasValue)
                                        (bg, fg, selected) = (@override.Value.bg, @override.Value.fg, @override.Value.selected);

                                    *vertexPtr++ = new FontVertex(
                                        new Vector4(x / context.Extent.AspectRatio, y, u0, v0), bg, fg, selected, fs.Offset);
                                    *vertexPtr++ = new FontVertex(
                                        new Vector4(x / context.Extent.AspectRatio, y + fs.Size, u0, v1), bg, fg, selected, fs.Offset);
                                    *vertexPtr++ = new FontVertex(
                                        new Vector4((x + xSize) / context.Extent.AspectRatio, y, u1, v0), bg, fg, selected, fs.Offset);
                                    *vertexPtr++ = new FontVertex(
                                        new Vector4((x + xSize) / context.Extent.AspectRatio, y + fs.Size, u1, v1), bg, fg, selected, fs.Offset);

                                    *indexPtr++ = (ushort)(vertexIdx + 0);
                                    *indexPtr++ = (ushort)(vertexIdx + 1);
                                    *indexPtr++ = (ushort)(vertexIdx + 2);

                                    *indexPtr++ = (ushort)(vertexIdx + 2);
                                    *indexPtr++ = (ushort)(vertexIdx + 1);
                                    *indexPtr++ = (ushort)(vertexIdx + 3);

                                    vertexIdx += 4;

                                    x += xSize;
                                }

                                ++charIndex;
                            }
                        }

                    resultCommandBuffer = context.CopyBuffer(stagingVertexIndexBuffer, vertexIndexBuffer,
                        MaxVertices * FontVertex.Size + UsedIndices * sizeof(ushort));

                    IsBufferDataDirty = false;
                }

                // update the UBO with the time and X/Y offset
                *(FontUniformObject*)perImageResources[nextImage].uniformBufferMemoryStartPtr =
                    new FontUniformObject { time = (float)totalElapsedTime.TotalMilliseconds };

                return resultCommandBuffer is null ? Array.Empty<CommandBuffer>() : new[] { resultCommandBuffer };
            }

            [SuppressMessage("Design", "CA1062:Validate arguments of public methods", Justification = "Called by framework")]
            public override void Draw(CommandBuffer commandBuffer, int idx)
            {
                commandBuffer.BindPipeline(PipelineBindPoint.Graphics, pipeline);
                commandBuffer.BindVertexBuffers(0, vertexIndexBuffer, 0);
                commandBuffer.BindIndexBuffer(vertexIndexBuffer, MaxVertices * FontVertex.Size, IndexType.Uint16);
                commandBuffer.BindDescriptorSets(PipelineBindPoint.Graphics, pipelineLayout, 0, perImageResources[idx].descriptorSet, null);
                commandBuffer.DrawIndexed(UsedIndices, 1, 0, 0, 0);
            }

            [SuppressMessage("Design", "CA1043:Use Integral Or String Argument For Indexers", Justification = "A char is integral")]
            public (float u0, float v0, float u1, float v1) this[char ch] => Characters[ch];

            public FontString AllocateString(Vector2 pos, float size)
            {
                var fs = new FontString(this, pos, size);
                fontStrings.Add(fs);
                IsBufferDataDirty = true;
                return fs;
            }

            public void FreeString(FontString fs) => fontStrings.Remove(fs);

            public void MoveStringToTop(FontString fontString)
            {
                if (fontStrings[^1] != fontString)
                {
                    fontStrings.Remove(fontString);
                    fontStrings.Add(fontString);
                    IsBufferDataDirty = true;
                }
            }

            public override void UpdateLogic(TimeSpan elapsedTime) => totalElapsedTime += elapsedTime;

            public override bool ProcessKey(Keys key, int scanCode, InputState action, ModifierKeys modifiers) => throw new NotImplementedException();

            public override bool ProcessCharacter(string character, ModifierKeys modifiers) => throw new NotImplementedException();

            public override bool ProcessMouseMove(double x, double y) => throw new NotImplementedException();

            public override bool ProcessMouseButton(MouseButton button, InputState action, ModifierKeys modifiers) => throw new NotImplementedException();

            #region IDisposable Support
            private bool disposedValue; // To detect redundant calls

            protected override void Dispose(bool disposing)
            {
                if (!disposedValue)
                {
                    if (disposing)
                    {
                        // managed resources
                    }

                    // native resources
                    fontTextureSampler.Dispose();
                    fontTextureImageView.Dispose();
                    fontTextureImage.Dispose();
                    fontTextureImageMemory.Free();
                    descriptorSetLayout.Dispose();
                    pipeline.Dispose();
                    pipelineLayout.Dispose();
                    perImageResources.ForEach(w =>
                    {
                        w.uniformBuffer.Dispose();
                        w.uniformBufferMemory.Free();
                    });
                    DisposeVertexIndexBuffers();

                    disposedValue = true;
                }
            }
            #endregion
        }

        public class FontString
        {
            readonly Font font;

            public FontString(Font font, Vector2 pos, float size)
            {
                this.font = font;
                position = pos;
                this.size = size;
            }

            public (FontColor bg, FontColor fg, bool selected)? TryGetOverrideForIndex(int needle)
            {
                if (Overrides is null || Overrides.Count == 0) return null;

                int min = 0, max = Overrides.Count, idx = (max - min) / 2;
                while (true)
                {
                    if (Overrides[idx].start <= needle && Overrides[idx].start + Overrides[idx].count > needle)
                        return (Overrides[idx].bg, Overrides[idx].fg, Overrides[idx].selected);

                    if (idx == max || idx == min) return null;

                    if (Overrides[idx].start + Overrides[idx].count <= needle)
                        min = idx;
                    else
                        max = idx;
                    idx = (max - min) / 2 + min;
                }
            }

            public void Set(string? value = null, FontColor? defaultBg = null, FontColor? defaultFg = null, Vector2? offset = null,
                IList<FontOverride>? overrides = null, bool? fillBackground = null)
            {
                if ((value is null || value == Value) && (!defaultBg.HasValue || defaultBg == DefaultBackground) &&
                    (!defaultFg.HasValue || defaultFg == DefaultForeground) && (!offset.HasValue || offset == Offset) &&
                    (!fillBackground.HasValue || fillBackground == FillBackground) &&
                    (overrides is null || (!(Overrides is null) && overrides.SequenceEqual(Overrides))))
                {
                    return;
                }

                int nonSpaceLengthNewValue = 0, width = 0, currentWidth = 0, height = 1;
                if (!(value is null))
                {
                    foreach (var ch in value)
                    {
                        if (ch != '\n' && ch != ' ' && ch != '\b')
                            ++nonSpaceLengthNewValue;
                        if (ch == '\b')
                            --currentWidth;
                        else if (ch == '\n')
                            (width, currentWidth, height) = (Math.Max(width, currentWidth), 0, height + 1);
                        else
                            ++currentWidth;
                    }

                    if (currentWidth > width) width = currentWidth;

                    (Width, Height) = (width, height);
                }

                font.IsCommandBufferDirty = Length != nonSpaceLengthNewValue || fillBackground.HasValue && FillBackground != fillBackground;

                if (!(value is null))
                    (Value, Length) = (value, nonSpaceLengthNewValue);
                if (defaultBg.HasValue) DefaultBackground = defaultBg.Value;
                if (defaultFg.HasValue) DefaultForeground = defaultFg.Value;
                if (!(overrides is null)) Overrides = overrides;
                if (offset.HasValue) Offset = offset.Value;
                if (fillBackground.HasValue) FillBackground = fillBackground.Value;

                Changed?.Invoke();

                font.IsBufferDataDirty = true;
            }

            public string? Value { get; private set; }

            public FontColor DefaultBackground { get; private set; }

            public FontColor DefaultForeground { get; private set; }

            public IList<FontOverride>? Overrides { get; private set; }

            public bool FillBackground { get; private set; }

            public Vector2 Offset { get; private set; }

            public int Length { get; private set; }

            public int Width { get; private set; }

            public int Height { get; private set; }

            Vector2 position;
            public Vector2 Position { get => position; set { position = value; font.IsBufferDataDirty = font.IsCommandBufferDirty = true; } }

            float size;

            public event Action? Changed;

            public float Size { get => size; set { size = value; font.IsBufferDataDirty = font.IsCommandBufferDirty = true; } }
        }

        struct FontUniformObject
        {
            public float time;

            public static readonly uint Size = (uint)Marshal.SizeOf<FontUniformObject>();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct FontVertex
        {
            public Vector4 posUv;
            public int bgFgSelected;
            public Vector2 offset;

            public FontVertex(Vector4 posUv, FontColor bg, FontColor fg, bool selected, Vector2 offset)
            {
                this.posUv = posUv;
                bgFgSelected = ((int)bg << 16) | ((int)fg << 8) | (selected ? 1 : 0);
                this.offset = offset;
            }

            public static readonly uint Size = (uint)Marshal.SizeOf<FontVertex>();

            public static readonly VertexInputBindingDescription BindingDescription =
                new VertexInputBindingDescription
                {
                    Binding = 0,
                    Stride = Size,
                    InputRate = VertexInputRate.Vertex
                };

            public static readonly VertexInputAttributeDescription[] AttributeDescriptions =
                new[]
                {
                    new VertexInputAttributeDescription
                    {
                        Binding = 0,
                        Location = 0,
                        Format = Format.R32G32B32A32SFloat,
                        Offset = (uint)Marshal.OffsetOf<FontVertex>(nameof(posUv)),
                    },
                    new VertexInputAttributeDescription
                    {
                        Binding = 0,
                        Location = 1,
                        Format = Format.R32UInt,
                        Offset = (uint)Marshal.OffsetOf<FontVertex>(nameof(bgFgSelected)),
                    },
                    new VertexInputAttributeDescription
                    {
                        Binding = 0,
                        Location = 2,
                        Format = Format.R32G32SFloat,
                        Offset = (uint)Marshal.OffsetOf<FontVertex>(nameof(offset)),
                    },
                };
        }
    }

    public enum FontColor
    {
        Black,
        DarkBlue,
        DarkGreen,
        DarkCyan,
        DarkRed,
        DarkMagenta,
        DarkYellow,
        DarkWhite,
        BrightBlack,
        BrightBlue,
        BrightGreen,
        BrightCyan,
        BrightRed,
        BrightMagenta,
        BrightYellow,
        White,
        Transparent
    }

    public struct FontOverride : IEquatable<FontOverride>
    {
        internal int start;
        internal int count;
        internal FontColor bg;
        internal FontColor fg;
        internal bool selected;

        public FontOverride(int start, int count, FontColor bg, FontColor fg, bool selected)
        {
            this.start = start;
            this.count = count;
            this.bg = bg;
            this.fg = fg;
            this.selected = selected;
        }

        public override int GetHashCode() => HashCode.Combine(start, count, bg, fg, selected);

        public override bool Equals(object? obj) => obj is FontOverride other && Equals(other);
        public bool Equals(FontOverride other) => start == other.start && count == other.count && bg == other.bg && fg == other.fg && selected == other.selected;
        public static bool operator ==(FontOverride left, FontOverride right) => left.Equals(right);
        public static bool operator !=(FontOverride left, FontOverride right) => !(left == right);
    }
}
