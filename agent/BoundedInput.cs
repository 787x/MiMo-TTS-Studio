namespace MiMoStudio.Agent;
// MCP stdio uses newline-delimited JSON. Bound each incoming message before SDK allocation.
public sealed class BoundedInput(Stream inner):Stream
{
    int lineBytes;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default)
    {
        int n=await inner.ReadAsync(buffer,ct);
        foreach(byte b in buffer.Span[..n]){if(b==10)lineBytes=0;else if(++lineBytes>128*1024)throw new InvalidDataException();}
        return n;
    }
    public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken ct)=>ReadAsync(buffer.AsMemory(offset,count),ct).AsTask();
    public override int Read(byte[] buffer,int offset,int count)=>ReadAsync(buffer,offset,count,default).GetAwaiter().GetResult();
    public override bool CanRead=>true; public override bool CanSeek=>false;public override bool CanWrite=>false;
    public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
    public override void Flush(){}public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
}
