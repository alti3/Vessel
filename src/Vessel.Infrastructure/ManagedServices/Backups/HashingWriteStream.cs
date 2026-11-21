using System.Security.Cryptography;

namespace Vessel.Infrastructure.ManagedServices.Backups;

/// <summary>
/// A write-only pass-through stream that computes the SHA-256 and byte count of everything written to the inner
/// stream, so artifacts are hashed while they stream to disk instead of being re-read or buffered.
/// </summary>
public sealed class HashingWriteStream(Stream inner, bool leaveOpen = false) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private string? _hex;

    public long BytesWritten { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => BytesWritten;

    public override long Position
    {
        get => BytesWritten;
        set => throw new NotSupportedException();
    }

    /// <summary>Finalizes the hash. No further writes are accepted afterwards.</summary>
    public string GetSha256Hex()
    {
        return _hex ??= Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureWritable();
        inner.Write(buffer);
        _hash.AppendData(buffer);
        BytesWritten += buffer.Length;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await inner.WriteAsync(buffer, cancellationToken);
        _hash.AppendData(buffer.Span);
        BytesWritten += buffer.Length;
    }

    public override void Flush()
    {
        inner.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return inner.FlushAsync(cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
            if (!leaveOpen) inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        _hash.Dispose();
        if (!leaveOpen) await inner.DisposeAsync();
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private void EnsureWritable()
    {
        if (_hex is not null) throw new InvalidOperationException("The hash has already been finalized.");
    }
}
