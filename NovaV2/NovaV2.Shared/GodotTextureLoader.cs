using System.Collections.Concurrent;
using System.IO;
using Godot;
using V12.Core;
using V12.Core.Interfaces;
using V12.Rendering;

namespace NovaV2.Hosting;

/// <summary>
/// CPU-side texture loader for the V12 contract. It resolves v12:// and
/// res:// paths, decodes the image with Godot, and returns tightly packed
/// RGBA8 <see cref="TextureData"/>. The renderer owns the GPU texture.
/// </summary>
public static class GodotTextureLoader
{
    private static readonly ConcurrentDictionary<string, TextureData> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> Failed = new(StringComparer.OrdinalIgnoreCase);

    public static TextureData? Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (Cache.TryGetValue(path, out var cached))
            return cached;
        if (Failed.ContainsKey(path))
            return null;

        var data = LoadCore(path);
        if (data is null)
        {
            Failed[path] = 0;
            GD.PushWarning($"[NovaV2] Could not load texture '{path}'.");
            return null;
        }

        Cache[path] = data;
        return data;
    }

    private static TextureData? LoadCore(string path)
    {
        string resolved = ResolvePath(path);
        Image? image = null;

        if (File.Exists(resolved))
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(resolved);
                image = Decode(resolved, bytes);
            }
            catch (Exception ex)
            {
                GD.PushWarning($"[NovaV2] Texture '{resolved}' read failed: {ex.Message}");
            }
        }
        else if (path.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
        {
            var img = new Image();
            if (img.Load(path) == Error.Ok)
                image = img;
        }

        if (image is null)
            return null;

        if (image.IsCompressed())
            image.Decompress();
        if (image.GetFormat() != Image.Format.Rgba8)
            image.Convert(Image.Format.Rgba8);

        int width = image.GetWidth();
        int height = image.GetHeight();
        if (width <= 0 || height <= 0)
            return null;

        byte[] pixels = image.GetData();
        int rowPitch = width * 4;
        var subresource = new TextureSubresource(0, 0, rowPitch, rowPitch * height, pixels);

        return new TextureData(
            TextureDimension.Texture2D,
            width,
            height,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureColorSpace.Linear,
            TextureUsage.Sampled,
            new[] { subresource });
    }

    private static Image? Decode(string filePath, byte[] bytes)
    {
        var image = new Image();
        Error error = Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".png" => image.LoadPngFromBuffer(bytes),
            ".jpg" or ".jpeg" => image.LoadJpgFromBuffer(bytes),
            ".webp" => image.LoadWebpFromBuffer(bytes),
            ".bmp" => image.LoadBmpFromBuffer(bytes),
            ".tga" => image.LoadTgaFromBuffer(bytes),
            ".ktx" => image.LoadKtxFromBuffer(bytes),
            ".dds" => image.LoadDdsFromBuffer(bytes),
            ".exr" => image.LoadExrFromBuffer(bytes),
            ".svg" => image.LoadSvgFromBuffer(bytes, 1f),
            _ => image.Load(filePath),
        };

        return error == Error.Ok ? image : null;
    }

    private static string ResolvePath(string path)
    {
        string resolved = V12AssetResolver.ResolveGlobal(path);
        if (!string.Equals(resolved, path, StringComparison.Ordinal))
            return resolved;

        var root = GameRoot.Instance;
        if (root?.Registry?.Get<IAssetResolver>() is V12AssetResolver resolver)
        {
            string relative = resolver.ResolveRelative(path);
            if (!string.Equals(relative, path, StringComparison.Ordinal))
                return relative;
        }

        if (path.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
            return ProjectSettings.GlobalizePath(path);

        if (File.Exists(path))
            return path;

        string stripped = StripResPrefix(path);
        if (File.Exists(stripped))
            return stripped;

        if (root is not null)
        {
            string fileName = Path.GetFileName(stripped);
            if (!string.IsNullOrEmpty(fileName))
            {
                foreach (var world in root.Worlds)
                {
                    if (string.IsNullOrEmpty(world.ExtractPath) || !Directory.Exists(world.ExtractPath))
                        continue;

                    foreach (var candidate in Directory.EnumerateFiles(
                                 world.ExtractPath,
                                 fileName,
                                 SearchOption.AllDirectories))
                    {
                        return candidate;
                    }
                }
            }
        }

        return resolved;
    }

    private static string StripResPrefix(string path) =>
        path.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
            ? path.Substring(6).TrimStart('/')
            : path;
}
