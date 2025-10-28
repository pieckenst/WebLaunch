using System.Numerics;
using Veldrid;

using System;
using System.Numerics;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

using Veldrid;
using Veldrid.ImageSharp;

namespace El_Garnan_Plugin_Loader.Rendering
{
    public sealed class TextureWrap : IDisposable
    {
        public IntPtr ImGuiHandle { get; }

        private readonly Texture _deviceTexture;

        public uint Width => _deviceTexture.Width;
        public uint Height => _deviceTexture.Height;

        public Vector2 Size => new(Width, Height);

        private TextureWrap(Texture texture, IntPtr handle)
        {
            _deviceTexture = texture;
            ImGuiHandle = handle;
        }

        public static TextureWrap Load(byte[] data, GraphicsDevice device, ImGuiBindings bindings)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            if (bindings == null) throw new ArgumentNullException(nameof(bindings));

            var image = Image.Load<Rgba32>(data);
            var imageTexture = new ImageSharpTexture(image, false);
            var deviceTexture = imageTexture.CreateDeviceTexture(device, device.ResourceFactory);
            var handle = bindings.GetOrCreateImGuiBinding(device.ResourceFactory, deviceTexture);
            return new TextureWrap(deviceTexture, handle);
        }

        public void Dispose()
        {
            _deviceTexture.Dispose();
        }
    }
}