using System;
using V12.Core.Interfaces;
using V12.Core.Rendering;
using V12.Core.UI.Paper;

namespace Rockstar
{
    public sealed class RaylibUIRenderer : IUIRenderer, IDisposable
    {
        private readonly RaylibPaperTextureRenderer _textures;
        private readonly RaylibPaperFrameRenderer _frameRenderer;
        private readonly ProwlPaperRenderer _paper;
        private readonly RaylibPaperInput _input = new();
        private int _width;
        private int _height;
        private bool _disposed;

        public RaylibUIRenderer(int width, int height)
        {
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);

            _textures = new RaylibPaperTextureRenderer();
            _frameRenderer = new RaylibPaperFrameRenderer();
            _paper = new ProwlPaperRenderer(_textures, _width, _height, 1f);
            _paper.WorldCanvasRendererFactory = (_, _, _) => new RaylibPaperTextureRenderer();
        }

        public ProwlPaperRenderer PaperRenderer => _paper;

        public PaperFrame? LastFrame => _paper.LastPaperFrame;

        public bool WantsCapturePointer => _paper.PaperInstance.WantsCapturePointer;

        public void ApplyUI(UIFrame frame) => _paper.ApplyUI(frame);

        public void step() => _paper.step();

        public void FeedInput(bool active) => _input.Feed(_paper.PaperInstance, active);

        public void Draw() => _frameRenderer.Draw(_paper.LastPaperFrame);

        public void Resize(int width, int height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            if (width == _width && height == _height) return;

            _width = width;
            _height = height;
            _paper.Resize(width, height, 1f);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _paper.WorldCanvases?.Dispose();
            _frameRenderer.Dispose();
            _textures.Dispose();
        }
    }
}
