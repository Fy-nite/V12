using System;
using System.IO;
using System.Linq;
using System.Text;
using V12.Hosting;
using V12.Pak;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// Split paks are a byte-level split of one archive. These cover writing parts,
/// reading them as one stream (including seeks across boundaries), and loading a
/// split build through the host's descriptor.
/// </summary>
public sealed class V12PakSplitTests : IDisposable
{
    private readonly string _root;

    public V12PakSplitTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "v12pak-split-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    private string NewDirectory(string name)
    {
        string directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static byte[] Incompressible(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>Builds a pak with an incompressible asset so a small split really splits.</summary>
    private (string Input, string Output, string PakPath, byte[] Blob, V12PakBuildResult Build) BuildSplit(int splitBytes = 512)
    {
        string input = NewDirectory("input");
        string output = NewDirectory("out");

        string worlds = Path.Combine(input, "worlds", "SplitWorld");
        Directory.CreateDirectory(worlds);
        File.WriteAllText(
            Path.Combine(worlds, "world.xml"),
            """
            <?xml version="1.0" encoding="utf-8"?>
            <World name="SplitWorld">
              <Element name="Marker" />
            </World>
            """);

        Directory.CreateDirectory(Path.Combine(input, "assets"));
        byte[] blob = Incompressible(8192, 1234);
        File.WriteAllBytes(Path.Combine(input, "assets", "blob.bin"), blob);

        string pakPath = Path.Combine(output, "SplitGame.v12pak");
        var build = V12PakBuilder.Build(input, pakPath, splitBytes);

        return (input, output, pakPath, blob, build);
    }

    [Fact]
    public void SplitBuildWritesContiguousParts()
    {
        var (_, _, pakPath, _, _) = BuildSplit();

        Assert.False(File.Exists(pakPath), "a split build must not leave a whole-pak file");

        var parts = V12PakParts.DiscoverParts(pakPath);
        Assert.True(parts.Count > 1);

        for (int i = 0; i < parts.Count; i++)
        {
            Assert.Equal(i + 1, V12PakParts.PartIndex(parts[i]));
            Assert.Equal(
                V12PakParts.PartPath(pakPath, i + 1),
                Path.GetFullPath(parts[i]));
        }

        Assert.True(parts.All(V12PakParts.IsPartFile));
    }

    [Fact]
    public void SplitBuildRecordsPartHashes()
    {
        var (_, _, _, _, build) = BuildSplit();

        Assert.True(build.IsSplit);
        Assert.Equal(build.Files.Count, build.PartInfo.Count);

        foreach (string file in build.Files)
        {
            var info = build.PartInfo[Path.GetFileName(file)];
            Assert.Equal(new FileInfo(file).Length, info.Bytes);
            Assert.Equal(V12PakParts.HashFile(file), info.Sha256);
        }
    }

    [Fact]
    public void VerifyRejectsACorruptedPart()
    {
        var (_, _, _, _, build) = BuildSplit();

        V12PakParts.Verify(build.Files, build.PartInfo);

        string second = build.Files[1];
        using (var stream = new FileStream(second, FileMode.Open, FileAccess.ReadWrite))
        {
            int first = stream.ReadByte();
            stream.Position = 0;
            stream.WriteByte((byte)(first ^ 0xFF));
        }

        Assert.Throws<InvalidDataException>(() => V12PakParts.Verify(build.Files, build.PartInfo));
    }

    [Fact]
    public void DescriptorRoundTripsPartInfo()
    {
        var (_, _, _, _, build) = BuildSplit();

        var descriptor = new V12GameDescriptor
        {
            Pak = "SplitGame.v12pak",
            Parts = build.Files.Select(file => Path.GetFileName(file)!).ToList(),
            PartInfo = new Dictionary<string, V12PakPartInfo>(build.PartInfo),
        };

        var parsed = V12GameDescriptor.Parse(V12GameDescriptor.Serialize(descriptor));

        Assert.NotNull(parsed?.PartInfo);
        Assert.Equal(build.PartInfo.Count, parsed!.PartInfo!.Count);

        foreach (var (name, info) in build.PartInfo)
            Assert.Equal(info, parsed.PartInfo[name]);
    }

    [Fact]
    public void SinglePartBuildKeepsThePlainName()
    {
        string input = NewDirectory("input-large");
        string output = NewDirectory("out-large");

        Directory.CreateDirectory(Path.Combine(input, "worlds", "PlainWorld"));
        File.WriteAllText(
            Path.Combine(input, "worlds", "PlainWorld", "world.xml"),
            """
            <?xml version="1.0" encoding="utf-8"?>
            <World name="PlainWorld"><Element name="Marker" /></World>
            """);

        string pakPath = Path.Combine(output, "PlainGame.v12pak");
        V12PakBuilder.Build(input, pakPath, splitBytes: 64L * 1024 * 1024);

        Assert.True(File.Exists(pakPath));
        Assert.Empty(V12PakParts.EnumeratePartFiles(pakPath));
        Assert.Single(V12PakParts.DiscoverParts(pakPath));
    }

    [Fact]
    public void MultiStreamMatchesTheConcatenatedParts()
    {
        var (_, _, pakPath, _, _) = BuildSplit();
        var parts = V12PakParts.DiscoverParts(pakPath);

        byte[] expected;
        using (var joined = new MemoryStream())
        {
            foreach (string part in parts)
            using (var stream = File.OpenRead(part))
                stream.CopyTo(joined);
            expected = joined.ToArray();
        }

        using var multi = V12PakMultiStream.Open(parts);
        Assert.Equal(expected.Length, multi.Length);

        using var actual = new MemoryStream();
        multi.CopyTo(actual);
        Assert.Equal(expected, actual.ToArray());

        // Seeks that land inside, at, and past a part boundary.
        int boundary = (int)new FileInfo(parts[0]).Length;
        foreach (int offset in new[] { 0, 1, boundary - 1, boundary, boundary + 1, expected.Length - 1 })
        {
            multi.Position = offset;
            Assert.Equal(expected[offset], multi.ReadByte());
        }
    }

    [Fact]
    public void ReaderExtractsASplitPak()
    {
        var (_, _, pakPath, blob, _) = BuildSplit();
        var parts = V12PakParts.DiscoverParts(pakPath);

        using var reader = new V12PakReader("SplitGame.v12pak", () => V12PakMultiStream.Open(parts));

        Assert.Contains("assets/blob.bin", reader.EntryNames);
        Assert.Equal("SplitWorld", Assert.Single(reader.Manifest.Worlds).Name);

        using var entry = reader.OpenEntry("assets/blob.bin");
        Assert.NotNull(entry);

        using var memory = new MemoryStream();
        entry!.CopyTo(memory);
        Assert.Equal(blob, memory.ToArray());
    }

    [Fact]
    public void HostLoadsASplitPakFromItsDescriptor()
    {
        var (_, output, pakPath, _, build) = BuildSplit();
        var parts = V12PakParts.DiscoverParts(pakPath);

        var descriptor = new V12GameDescriptor
        {
            Pak = Path.GetFileName(pakPath),
            Parts = parts.Select(part => Path.GetFileName(part)!).ToList(),
            PartInfo = new Dictionary<string, V12PakPartInfo>(build.PartInfo, StringComparer.OrdinalIgnoreCase),
            Engine = "v12",
            World = "SplitWorld",
        };
        V12GameDescriptor.Save(Path.Combine(output, V12GameDescriptor.FileName), descriptor);

        var options = new V12HostOptions
        {
            ContentDirectory = output,
            UserDirectory = NewDirectory("user"),
            InitializeRoot = false,
            UseExecutableNameFallback = false,
            VerifyParts = true,
            PakOptions = new V12PakOptions
            {
                LoadDlls = false,
                LoadContractScripts = false,
                LoadCSharpScripts = false,
            },
        };

        using var host = new V12Host(options).Start();

        Assert.NotNull(host.Source);
        Assert.Equal("descriptor", host.Source!.Origin);
        Assert.True(host.Source.IsSplit);
        Assert.Equal(parts.Count, host.Source.Files.Count);
        Assert.Equal("SplitWorld", host.Root.SelectedWorld?.WorldName);
    }

    [Fact]
    public void PartPatternIsNotAWholePak()
    {
        Assert.True(V12PakParts.IsPartFile("Game.part001.v12pak"));
        Assert.True(V12PakParts.IsPartFile("Game.part1200.v12pak"));
        Assert.False(V12PakParts.IsPartFile("Game.v12pak"));
        Assert.False(V12PakParts.IsPartFile("part001.v12pak"));
        Assert.False(V12PakParts.IsPartFile("Game.part1.v12pak"));
    }

    public void Dispose()
    {
        V12PakCache.Purge();

        try { Directory.Delete(_root, true); }
        catch
        {
            // Best effort; the OS temp directory is disposable.
        }
    }
}
