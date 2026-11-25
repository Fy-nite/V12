using System;
using System.IO;
using Godot;
using GodotFileAccess = Godot.FileAccess;

namespace NovaV2.Hosting;

/// <summary>
/// Read-only, seekable <see cref="Stream"/> over a Godot path
/// (<c>res://</c>, <c>user://</c> or a real path). An exported Android build
/// keeps its content inside the APK, where <see cref="System.IO"/> cannot see
/// it, but <see cref="FileAccess"/> can. <see cref="V12.Pak.V12PakReader"/>
/// accepts this stream so the archive can be extracted straight into the cache
/// without copying the whole pak out first.
/// </summary>
public sealed class GodotPakStream : Stream
{
    private readonly GodotFileAccess _file;
    private readonly long _length;

    private GodotPakStream(GodotFileAccess file)
    {
        _file = file;
        _length = (long)file.GetLength();
    }

    /// <summary>Opens <paramref name="path"/> for reading, or throws.</summary>
    public static GodotPakStream Open(string path)
    {
        var file = GodotFileAccess.Open(path, GodotFileAccess.ModeFlags.Read)
            ?? throw new FileNotFoundException($"Could not open '{path}' for reading.", path);

        return new GodotPakStream(file);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => (long)_file.GetPosition();
        set => _file.Seek((ulong)Math.Clamp(value, 0, _length));
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0)
            return 0;

        // GetBuffer returns an empty array at EOF, which is what Stream wants.
        byte[] chunk = _file.GetBuffer(buffer.Length);
        int read = Math.Min(chunk.Length, buffer.Length);
        if (read == 0)
            return 0;

        chunk.AsSpan(0, read).CopyTo(buffer);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => (long)_file.GetPosition() + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        if (target < 0)
            throw new IOException("Attempted to seek before the start of the pak.");

        _file.Seek((ulong)target);
        return target;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _file.Dispose();

        base.Dispose(disposing);
    }
}
