using System;
using System.Collections.Generic;
using Raylib_cs;
using V12.Core.UI.Paper;
using Int2 = Prowl.Vector.Int2;
using IntRect = Prowl.Vector.IntRect;

namespace Rockstar
{
    public sealed class RaylibPaperTextureRenderer : PaperCanvasRenderer
    {
        private readonly List<Texture2D> _owned = new();

        public override object CreateTexture(uint width, uint height)
        {
            int w = Math.Max(1, (int)width);
            int h = Math.Max(1, (int)height);
            var image = Raylib.GenImageColor(w, h, new Color(0, 0, 0, 0));
            var texture = Raylib.LoadTextureFromImage(image);
            Raylib.UnloadImage(image);
            _owned.Add(texture);
            return texture;
        }

        public override Int2 GetTextureSize(object texture)
        {
            if (texture is Texture2D t)
                return new Int2(t.Width, t.Height);
            return base.GetTextureSize(texture);
        }

        public override void SetTextureData(object texture, IntRect bounds, byte[] data)
        {
            if (texture is not Texture2D target)
            {
                base.SetTextureData(texture, bounds, data);
                return;
            }

            int x = Math.Max(0, bounds.Min.X);
            int y = Math.Max(0, bounds.Min.Y);
            int w = Math.Max(0, bounds.Max.X - x);
            int h = Math.Max(0, bounds.Max.Y - y);
            if (w <= 0 || h <= 0) return;

            if (data.Length == w * h * 4)
            {
                Raylib.UpdateTextureRec(target, new Rectangle(x, y, w, h), data);
                return;
            }

            if (data.Length == target.Width * target.Height * 4)
            {
                Raylib.UpdateTexture(target, data);
                return;
            }

            int pixels = data.Length / 4;
            int size = (int)Math.Sqrt(pixels);
            if (size <= 0 || size * size != pixels) return;
            Raylib.UpdateTextureRec(target, new Rectangle(0, 0, size, size), data);
        }

        public override void Dispose()
        {
            base.Dispose();
            foreach (var texture in _owned)
            {
                if (texture.Id != 0)
                    Raylib.UnloadTexture(texture);
            }
            _owned.Clear();
        }
    }
}
