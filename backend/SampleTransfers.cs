namespace Studio;

public sealed class SampleTransfers(Storage storage)
{
    private readonly SemaphoreSlim slots = new(2);
    public async Task<IResult> Upload(HttpContext context, string owner)
    {
        if (!await slots.WaitAsync(0, context.RequestAborted)) return Results.Json(new { error = "已有两个样本处理请求，请稍后再试。" }, statusCode: 429);
        try
        {
            string name = Storage.SampleName(Uri.UnescapeDataString(context.Request.Headers["X-Sample-Name"].ToString()));
            string format = context.Request.Headers["X-Sample-Format"].ToString();
            _ = SampleAudio.Mime(format);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var content = new MemoryStream();
            var block = new byte[65536]; int n;
            while ((n = await context.Request.Body.ReadAsync(block, timeout.Token)) > 0)
            {
                if (content.Length + n > SampleAudio.MaxBytes) return Results.StatusCode(413);
                content.Write(block, 0, n);
            }
            var sample = storage.CreateSample(owner, name, format, content.ToArray(), ct: timeout.Token);
            return Results.Json(sample, statusCode: 201);
        }
        catch (OperationCanceledException) { return Results.Json(new { error = "样本上传已取消或超时，没有保存样本。" }, statusCode: 408); }
        finally { slots.Release(); }
    }
    public async Task<IResult> FromAsset(HttpContext context, string id, string owner, string name)
    {
        if (!await slots.WaitAsync(0, context.RequestAborted)) return Results.Json(new { error = "样本处理忙，请稍后再试。" }, statusCode: 429);
        try { return storage.SampleFromAsset(id, owner, name) is { } sample ? Results.Json(sample, statusCode: 201) : Results.NotFound(); }
        finally { slots.Release(); }
    }
}
