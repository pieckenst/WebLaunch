using System;
using System.IO;
using System.Numerics;
using StbImageSharp;
using Veldrid;

namespace El_Garnan_Plugin_Loader.Rendering
{
    /// <summary>
    /// Owns a GPU texture for an RGBA-decoded image and the ImGui binding used to display it.
    /// Image decoding is deliberately independent of SixLabors.ImageSharp so open-source builds
    /// do not require proprietary build credentials.
    /// </summary>
    public sealed class TextureWrap : IDisposable
    {
        public IntPtr ImGuiHandle { get; }

        private readonly Texture _deviceTexture;
        private readonly ImGuiBindings _bindings;
        private bool _disposed;

        public uint Width => _deviceTexture.Width;
        public uint Height => _deviceTexture.Height;

        public Vector2 Size => new(Width, Height);

        private TextureWrap(Texture texture, IntPtr handle, ImGuiBindings bindings)
        {
            _deviceTexture = texture;
            ImGuiHandle = handle;
            _bindings = bindings;
        }

        /// <summary>
        /// Decodes supported image bytes to a contiguous 8-bit RGBA buffer.
        /// Kept separately so malformed content and dimension overflow can be tested without a GPU.
        /// </summary>
        /// <param name="data">Encoded JPG, PNG, BMP, TGA, PSD or GIF bytes.</param>
        /// <returns>Decoded RGBA pixels and dimensions.</returns>
        /// <exception cref="ArgumentNullException">The input buffer is null.</exception>
        /// <exception cref="InvalidDataException">The decoder returned invalid dimensions or an inconsistent pixel buffer.</exception>
        internal static ImageResult DecodeRgba(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            var image = ImageResult.FromMemory(data, ColorComponents.RedGreenBlueAlpha);
            if (image.Width <= 0 || image.Height <= 0)
                throw new InvalidDataException("Decoded image dimensions must be positive.");

            var expectedBytes = checked((long)image.Width * image.Height * 4);
            if (expectedBytes > int.MaxValue || image.Data.Length != expectedBytes)
                throw new InvalidDataException("Decoded RGBA pixel buffer does not match its dimensions.");

            return image;
        }

        /// <summary>Decodes an image and uploads it as a single-level, non-sRGB RGBA texture.</summary>
        /// <param name="data">Encoded image data.</param>
        /// <param name="device">Graphics device that owns the resulting texture.</param>
        /// <param name="bindings">ImGui resource bindings used to resolve a texture handle.</param>
        /// <returns>An owning wrapper for the device texture and ImGui handle.</returns>
        public static TextureWrap Load(byte[] data, GraphicsDevice device, ImGuiBindings bindings)
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentNullException.ThrowIfNull(bindings);

            var image = DecodeRgba(data);
            var width = checked((uint)image.Width);
            var height = checked((uint)image.Height);
            var texture = device.ResourceFactory.CreateTexture(TextureDescription.Texture2D(
                width,
                height,
                mipLevels: 1,
                arrayLayers: 1,
                format: PixelFormat.R8_G8_B8_A8_UNorm,
                usage: TextureUsage.Sampled));

            try
            {
                device.UpdateTexture(
                    texture,
                    image.Data,
                    x: 0,
                    y: 0,
                    z: 0,
                    width: width,
                    height: height,
                    depth: 1,
                    mipLevel: 0,
                    arrayLayer: 0);

                var handle = bindings.GetOrCreateImGuiBinding(device.ResourceFactory, texture);
                return new TextureWrap(texture, handle, bindings);
            }
            catch
            {
                bindings.RemoveImGuiBinding(texture);
                texture.Dispose();
                throw;
            }
        }

        /// <summary>Releases the ImGui binding and owned GPU texture.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _bindings.RemoveImGuiBinding(_deviceTexture);
            _deviceTexture.Dispose();
            _disposed = true;
        }
    }
}
