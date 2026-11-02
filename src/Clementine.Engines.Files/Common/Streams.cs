namespace Clementine.Engines.Files.Common;

/// <summary>Pass-through stream that never disposes the wrapped stream (for libraries that insist on closing).</summary>
internal sealed class NonClosingStream(Stream inner) : Stream
{
    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => inner.Read(buffer);

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
}

/// <summary>
/// Read-only seekable wrapper over a file that remembers the furthest position read, so progress can be reported
/// as "how much of the archive has been consumed" whatever the library does with the stream.
/// </summary>
internal sealed class ProgressStream(Stream inner, bool leaveOpen = false) : Stream
{
    long _furthest;

    public double Fraction => inner.Length <= 0 ? 0 : Math.Clamp((double)_furthest / inner.Length, 0, 1);

    public override bool CanRead => true;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Track(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Track(inner.Read(buffer));

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !leaveOpen)
            inner.Dispose();
        base.Dispose(disposing);
    }

    int Track(int read)
    {
        if (read > 0 && inner.CanSeek)
        {
            var position = inner.Position;
            if (position > _furthest)
                _furthest = position;
        }
        return read;
    }
}

/// <summary>Read-only wrapper that reports bytes read through a callback (used while writing archives).</summary>
internal sealed class ReadCallbackStream(Stream inner, Action<int> onRead, CancellationToken cancellationToken) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Report(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Report(inner.Read(buffer));

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }

    int Report(int read)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (read > 0)
            onRead(read);
        return read;
    }
}

/// <summary>
/// Presents exactly <see cref="Length"/> bytes of a file that another program may still be writing: reads stop at
/// the length the file had when it was opened, and a file that shrank meanwhile is padded with zeros. TAR stores
/// each size in a header written <em>before</em> the data, so a growing log or recording would otherwise shift
/// every later entry and corrupt the archive.
/// </summary>
internal sealed class FixedLengthStream : Stream
{
    readonly Stream _inner;
    readonly long _length;
    long _position;

    public FixedLengthStream(Stream inner)
    {
        _inner = inner;
        _length = inner.Length;
    }

    /// <summary>True once the file was seen to grow or shrink while it was being read.</summary>
    public bool Changed { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var wanted = (int)Math.Min(buffer.Length, _length - _position);
        if (wanted <= 0)
        {
            if (!Changed && _inner.Length != _length)
                Changed = true;
            return 0;
        }
        var read = _inner.Read(buffer[..wanted]);
        if (read == 0)
        {
            // The file shrank: keep the promised size so the archive stays readable.
            Changed = true;
            buffer[..wanted].Clear();
            read = wanted;
        }
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        if (target < 0 || target > _length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        _inner.Position = Math.Min(target, _inner.Length);
        _position = target;
        return target;
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }
}
