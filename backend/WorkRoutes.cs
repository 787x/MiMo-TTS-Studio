namespace Studio;

public static class WorkRoutes
{
    public static void MapWorks(this WebApplication app)
    {
        var routes = app.MapGroup("/api/works");
        static string Owner(HttpContext c) => c.Request.Headers["X-Trim-Userid"].ToString();
        routes.MapGet("/", (HttpContext c, Works works, int? offset) => works.List(Owner(c), offset ?? 0));
        routes.MapPost("/", (HttpContext c, Works works, WorkCreate input) => works.Create(Owner(c), input));
        routes.MapGet("/{id}", (HttpContext c, Works works, string id) => works.Get(id, Owner(c)));
        routes.MapDelete("/{id}", (HttpContext c, Works works, string id) => { works.Delete(id, Owner(c)); return Results.NoContent(); });
        routes.MapPost("/{id}/text/bind", (HttpContext c, Works works, string id, WorkUploadPlan input) =>
            works.BindUpload(id, Owner(c), input, c.RequestAborted));
        routes.MapPut("/{id}/text", async (HttpContext c, Works works, string id, long offset) =>
        {
            var body = new byte[Works.ChunkBytes + 1];
            int count = await c.Request.Body.ReadAtLeastAsync(body, body.Length, false, c.RequestAborted);
            if (count > Works.ChunkBytes) throw new InputError("分块不能超过 64 KiB。");
            return works.Upload(id, Owner(c), offset, body[..count], c.Request.Headers["X-Chunk-SHA256"].ToString());
        });
        routes.MapPost("/{id}/prepare", (HttpContext c, Works works, string id, WorkPrepare input) => works.Prepare(id, Owner(c), input));
        routes.MapGet("/{id}/preview", (HttpContext c, Works works, string id, long? offset) => works.Preview(id, Owner(c), offset ?? 0));
        routes.MapGet("/{id}/chapters", (HttpContext c, Works works, string id, long? offset) => works.Chapters(id, Owner(c), offset ?? 0));
        routes.MapGet("/{id}/chapters/{number:long}/download", (HttpContext c, Works works, string id, long number) =>
            Results.Stream(output => works.WriteChapter(id, Owner(c), number, output, c.RequestAborted),
                "application/x-tar", $"mimo-{id}-chapter-{number + 1}.tar"));
        routes.MapPost("/{id}/control", (HttpContext c, Works works, string id, WorkAction input) => works.Control(id, Owner(c), input));
        routes.MapGet("/{id}/volumes", (HttpContext c, Works works, string id, long? offset, long? chapter) => works.Playlist(id, Owner(c), offset ?? 0, chapter));
        routes.MapGet("/{id}/segments/{number:long}", (HttpContext c, Works works, string id, long number) =>
            Results.Stream(works.OpenSegment(id, Owner(c), number), "audio/wav", enableRangeProcessing: true));
        routes.MapGet("/{id}/volumes/{number:long}", (HttpContext c, Works works, string id, long number) =>
            Results.Stream(works.OpenVolume(id, Owner(c), number).File, "audio/wav",
                c.Request.Query.ContainsKey("download") ? $"mimo-{id}-{number + 1}.wav" : null, enableRangeProcessing: true));
        routes.MapPost("/{id}/volumes/{number:long}/export", (HttpContext c, Works works, NasExport exports, string id, long number, ExportInput input) =>
            exports.Copy(Owner(c), input, () => works.OpenVolume(id, Owner(c), number), c.RequestAborted, FormattableString.Invariant($"{id}/{number}")));
    }
}
