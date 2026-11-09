using System;
using System.Numerics;
using Raylib_cs;
using V12.Core.UI.Paper;

namespace Rockstar
{
    public sealed class RaylibPaperFrameRenderer : IDisposable
    {
        private const string VertexShader = @"
#version 330
layout(location = 0) in vec3 vertexPosition;
layout(location = 1) in vec2 vertexTexCoord;
layout(location = 3) in vec4 vertexColor;

uniform vec2 viewportSize;

out vec2 fragPos;
out vec2 fragUV;
out vec4 fragColor;

void main()
{
    fragPos = vertexPosition.xy;
    fragUV = vertexTexCoord;
    fragColor = vertexColor;

    gl_Position = vec4(
        vertexPosition.x / viewportSize.x * 2.0 - 1.0,
        1.0 - vertexPosition.y / viewportSize.y * 2.0,
        0.0,
        1.0);
}
";

        private const string FragmentShader = @"
#version 330
in vec2 fragPos;
in vec2 fragUV;
in vec4 fragColor;

uniform sampler2D texture0;
uniform sampler2D fontTexture;

uniform vec4 scissorTransform;
uniform vec2 scissorTranslation;
uniform vec2 scissorExt;

uniform vec4 brushTransform;
uniform vec2 brushTranslation;
uniform int brushType;
uniform vec4 brushColor1;
uniform vec4 brushColor2;
uniform vec4 brushParams;
uniform vec2 brushParams2;

uniform vec4 textureTransform;
uniform vec2 textureTranslation;

uniform vec2 atlasTexelSize;
uniform float sdfPxRange;

out vec4 finalColor;

vec2 ApplyTransform(vec4 t, vec2 translation, vec2 p)
{
    return vec2(dot(t.xy, p), dot(t.zw, p)) + translation;
}

float BrushFactor(vec2 p)
{
    vec2 q = ApplyTransform(brushTransform, brushTranslation, p);

    if (brushType == 1)
    {
        vec2 start = brushParams.xy;
        vec2 dir = brushParams.zw - start;
        float len = length(dir);
        if (len < 0.001) return 0.0;
        return clamp(dot(q - start, dir) / (len * len), 0.0, 1.0);
    }

    if (brushType == 2)
    {
        return clamp(smoothstep(brushParams.z, brushParams.w, length(q - brushParams.xy)), 0.0, 1.0);
    }

    if (brushType == 3)
    {
        vec2 center = brushParams.xy;
        vec2 halfSize = brushParams.zw;
        float radius = brushParams2.x;
        float feather = brushParams2.y;
        if (halfSize.x < 0.001 || halfSize.y < 0.001) return 0.0;

        vec2 d = abs(q - center) - (halfSize - vec2(radius, radius));
        float dist = min(max(d.x, d.y), 0.0) + length(max(d, vec2(0.0))) - radius;
        return smoothstep(-feather * 0.5, feather * 0.5, dist);
    }

    return 0.0;
}

float ScissorMask(vec2 p)
{
    if (scissorExt.x < 0.0 || scissorExt.y < 0.0) return 1.0;
    vec2 smoothEdges = scissorExt - abs(ApplyTransform(scissorTransform, scissorTranslation, p));
    return clamp(smoothEdges.x, 0.0, 1.0) * clamp(smoothEdges.y, 0.0, 1.0);
}

float SdfScreenPxRange(vec2 uv)
{
    vec2 unitRange = vec2(sdfPxRange) * atlasTexelSize;
    vec2 screenTexSize = vec2(1.0) / max(fwidth(uv), vec2(0.00001));
    vec2 range = unitRange * screenTexSize;
    return max(min(range.x, range.y), 1.0);
}

void main()
{
    float mask = ScissorMask(fragPos);

    vec4 color = fragColor;
    if (brushType > 0)
        color = mix(brushColor1, brushColor2, vec4(BrushFactor(fragPos)));

    if (fragUV.x >= 2.0)
    {
        vec2 uv = fragUV - 2.0;
        float distance = texture(fontTexture, uv).x;
        float coverage = clamp(SdfScreenPxRange(uv) * (distance - 0.5) + 0.5, 0.0, 1.0);
        finalColor = color * coverage * mask;
        return;
    }

    float edgeAlpha = clamp(fragUV.x, 0.0, 1.0);
    vec4 fill = color * texture(texture0, ApplyTransform(textureTransform, textureTranslation, fragPos));
    finalColor = fill * edgeAlpha * mask;
}
";

        private readonly Shader _shader;
        private readonly Texture2D _white;
        private readonly int _uViewportSize;
        private readonly int _uTexture0;
        private readonly int _uFontTexture;
        private readonly int _uScissorTransform;
        private readonly int _uScissorTranslation;
        private readonly int _uScissorExt;
        private readonly int _uBrushTransform;
        private readonly int _uBrushTranslation;
        private readonly int _uBrushType;
        private readonly int _uBrushColor1;
        private readonly int _uBrushColor2;
        private readonly int _uBrushParams;
        private readonly int _uBrushParams2;
        private readonly int _uTextureTransform;
        private readonly int _uTextureTranslation;
        private readonly int _uAtlasTexelSize;
        private readonly int _uSdfPxRange;

        public RaylibPaperFrameRenderer()
        {
            _shader = Raylib.LoadShaderFromMemory(VertexShader, FragmentShader);
            if (!Raylib.IsShaderValid(_shader))
                throw new InvalidOperationException("Failed to load the Paper UI shader.");

            var image = Raylib.GenImageColor(1, 1, Color.White);
            _white = Raylib.LoadTextureFromImage(image);
            Raylib.UnloadImage(image);

            _uViewportSize = Raylib.GetShaderLocation(_shader, "viewportSize");
            _uTexture0 = Raylib.GetShaderLocation(_shader, "texture0");
            _uFontTexture = Raylib.GetShaderLocation(_shader, "fontTexture");
            _uScissorTransform = Raylib.GetShaderLocation(_shader, "scissorTransform");
            _uScissorTranslation = Raylib.GetShaderLocation(_shader, "scissorTranslation");
            _uScissorExt = Raylib.GetShaderLocation(_shader, "scissorExt");
            _uBrushTransform = Raylib.GetShaderLocation(_shader, "brushTransform");
            _uBrushTranslation = Raylib.GetShaderLocation(_shader, "brushTranslation");
            _uBrushType = Raylib.GetShaderLocation(_shader, "brushType");
            _uBrushColor1 = Raylib.GetShaderLocation(_shader, "brushColor1");
            _uBrushColor2 = Raylib.GetShaderLocation(_shader, "brushColor2");
            _uBrushParams = Raylib.GetShaderLocation(_shader, "brushParams");
            _uBrushParams2 = Raylib.GetShaderLocation(_shader, "brushParams2");
            _uTextureTransform = Raylib.GetShaderLocation(_shader, "textureTransform");
            _uTextureTranslation = Raylib.GetShaderLocation(_shader, "textureTranslation");
            _uAtlasTexelSize = Raylib.GetShaderLocation(_shader, "atlasTexelSize");
            _uSdfPxRange = Raylib.GetShaderLocation(_shader, "sdfPxRange");

        }

        public void Draw(PaperFrame? frame)
        {
            if (frame == null || frame.Meshes.Count == 0) return;

            // New PaperFrame no longer has FramebufferScale/Viewports; use Width/Height directly.
            float scale = 1f;

            Raylib.SetShaderValue(_shader, _uViewportSize,
                new[] { frame.Width * scale, frame.Height * scale },
                ShaderUniformDataType.Vec2);

            Rlgl.SetBlendMode(BlendMode.AlphaPremultiply);
            Rlgl.DisableDepthTest();
            Rlgl.DisableBackfaceCulling();

            foreach (var mesh in frame.Meshes)
            {
                if (mesh.Indices.Length == 0) continue;

                // New PaperMesh stores TextureId as string hash; actual GPU texture is owned by
                // RaylibPaperTextureRenderer. For headless fallback we just use the white texture.
                // Viewport textures and font atlases are also white in this simplified path.
                Texture2D texture = _white;
                var font = _white;

                Raylib.BeginShaderMode(_shader);
                Rlgl.Begin(DrawMode.Triangles);
                Rlgl.SetTexture(texture.Id);
                Raylib.SetShaderValueTexture(_shader, _uFontTexture, font);

                ApplyMeshUniforms(mesh);

                for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
                {
                    int i0 = (int)mesh.Indices[i];
                    int i1 = (int)mesh.Indices[i + 1];
                    int i2 = (int)mesh.Indices[i + 2];
                    if ((uint)i0 >= (uint)mesh.Positions.Length
                        || (uint)i1 >= (uint)mesh.Positions.Length
                        || (uint)i2 >= (uint)mesh.Positions.Length)
                        continue;

                    EmitVertex(mesh, i0);
                    EmitVertex(mesh, i1);
                    EmitVertex(mesh, i2);
                }

                Rlgl.End();
                Rlgl.DrawRenderBatchActive();
                Raylib.EndShaderMode();
            }

            Rlgl.SetTexture(0);
            Rlgl.EnableDepthTest();
            Rlgl.EnableBackfaceCulling();
            Rlgl.SetBlendMode(BlendMode.Alpha);
        }

        private static void EmitVertex(PaperMesh mesh, int index)
        {
            var p = mesh.Positions[index];
            var uv = mesh.UVs[index];
            var c = mesh.Colors[index];

            Rlgl.Color4ub(ToByte(c.R), ToByte(c.G), ToByte(c.B), ToByte(c.A));
            Rlgl.TexCoord2f(uv.X, uv.Y);
            Rlgl.Vertex3f(p.X, p.Y, 0f);
        }

        private void ApplyMeshUniforms(PaperMesh mesh)
        {
            // New PaperMesh (Engine 1.x) no longer carries per-mesh scissor/brush/texture matrices.
            // Feed the shader with identity/defaults so the shared vertex buffer still renders.
            // Brush/Scissor features are effectively disabled; the base vertex colors still apply.
            Raylib.SetShaderValue(_shader, _uScissorTransform,
                new[] { 1f, 0f, 0f, 1f },
                ShaderUniformDataType.Vec4);
            Raylib.SetShaderValue(_shader, _uScissorTranslation,
                new[] { 0f, 0f },
                ShaderUniformDataType.Vec2);
            Raylib.SetShaderValue(_shader, _uScissorExt,
                new[] { -1f, -1f },
                ShaderUniformDataType.Vec2);

            Raylib.SetShaderValue(_shader, _uBrushTransform,
                new[] { 1f, 0f, 0f, 1f },
                ShaderUniformDataType.Vec4);
            Raylib.SetShaderValue(_shader, _uBrushTranslation,
                new[] { 0f, 0f },
                ShaderUniformDataType.Vec2);
            Raylib.SetShaderValue(_shader, _uBrushType, 0, ShaderUniformDataType.Int);
            SetColorUniform(_uBrushColor1, 0xFFFFFFFF);
            SetColorUniform(_uBrushColor2, 0xFFFFFFFF);
            Raylib.SetShaderValue(_shader, _uBrushParams,
                new[] { 0f, 0f, 0f, 0f },
                ShaderUniformDataType.Vec4);
            Raylib.SetShaderValue(_shader, _uBrushParams2,
                new[] { 0f, 0f },
                ShaderUniformDataType.Vec2);

            Raylib.SetShaderValue(_shader, _uTextureTransform,
                new[] { 1f, 0f, 0f, 1f },
                ShaderUniformDataType.Vec4);
            Raylib.SetShaderValue(_shader, _uTextureTranslation,
                new[] { 0f, 0f },
                ShaderUniformDataType.Vec2);

            Raylib.SetShaderValue(_shader, _uAtlasTexelSize,
                new[] { 1f, 1f },
                ShaderUniformDataType.Vec2);
            Raylib.SetShaderValue(_shader, _uSdfPxRange, 4f, ShaderUniformDataType.Float);
        }

        private void SetColorUniform(int location, uint rgba)
        {
            Raylib.SetShaderValue(_shader, location,
                new[]
                {
                    (rgba & 0xFF) / 255f,
                    ((rgba >> 8) & 0xFF) / 255f,
                    ((rgba >> 16) & 0xFF) / 255f,
                    ((rgba >> 24) & 0xFF) / 255f,
                },
                ShaderUniformDataType.Vec4);
        }

        private static byte ToByte(float value) =>
            (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);

        public void Dispose()
        {
            if (Raylib.IsTextureValid(_white))
                Raylib.UnloadTexture(_white);
            if (Raylib.IsShaderValid(_shader))
                Raylib.UnloadShader(_shader);
        }
    }
}
