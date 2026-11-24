using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "PdfSplitterApi";
});
builder.Services.AddOpenApi();
builder.Services.AddAntiforgery(o => o.SuppressXFrameOptionsHeader = true);

var app = builder.Build();
app.MapOpenApi();

// ---------------------------------------------------------------------------
// Config
// ---------------------------------------------------------------------------

var apiKeysRaw = Environment.GetEnvironmentVariable("API_KEYS") ?? "";
var apiKeys = apiKeysRaw
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .ToHashSet();

// ---------------------------------------------------------------------------
// Middleware auth
// ---------------------------------------------------------------------------

app.Use(async (context, next) =>
{
    if (apiKeys.Count == 0)
    {
        await next();
        return;
    }

    if (context.Request.Path.StartsWithSegments("/health"))
    {
        await next();
        return;
    }

    var key = context.Request.Headers["X-API-Key"].FirstOrDefault();
    if (string.IsNullOrEmpty(key) || !apiKeys.Any(k =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(key),
            Encoding.UTF8.GetBytes(k))))
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsJsonAsync(new { detail = "Clé API invalide ou manquante." });
        return;
    }

    await next();
});

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

static PdfDocument OpenPdf(Stream stream)
{
    return PdfReader.Open(stream, PdfDocumentOpenMode.Import);
}

static byte[] WritePdf(PdfDocument doc)
{
    using var ms = new MemoryStream();
    doc.Save(ms);
    return ms.ToArray();
}

static byte[] ExtractPages(PdfDocument src, List<int> indices)
{
    var dst = new PdfDocument();
    foreach (var i in indices)
        dst.AddPage(src.Pages[i]);
    return WritePdf(dst);
}

static byte[] MakeZip(Dictionary<string, byte[]> files)
{
    using var ms = new MemoryStream();
    using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
    {
        foreach (var (name, data) in files)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var es = entry.Open();
            es.Write(data);
        }
    }
    return ms.ToArray();
}

static List<int> ParsePages(string spec, int pageCount)
{
    var result = new HashSet<int>();
    foreach (var raw in spec.Split(','))
    {
        var part = raw.Trim().ToLower();
        if (string.IsNullOrEmpty(part)) continue;

        if (part == "last")
        {
            result.Add(pageCount - 1);
        }
        else if (part == "odd")
        {
            for (int i = 0; i < pageCount; i += 2) result.Add(i);
        }
        else if (part == "even")
        {
            for (int i = 1; i < pageCount; i += 2) result.Add(i);
        }
        else if (part.Contains('-'))
        {
            var parts = part.Split('-', 2);
            int start = string.IsNullOrEmpty(parts[0]) ? 1 : int.Parse(parts[0]);
            int end = string.IsNullOrEmpty(parts[1]) ? pageCount : int.Parse(parts[1]);
            if (start < 1 || end < 1 || start > pageCount)
                throw new ArgumentException($"Plage invalide '{part}' pour un PDF de {pageCount} pages.");
            end = Math.Min(end, pageCount);
            for (int i = start - 1; i < end; i++) result.Add(i);
        }
        else
        {
            int p = int.Parse(part);
            if (p < 1 || p > pageCount)
                throw new ArgumentException($"Page {p} hors limites (1-{pageCount}).");
            result.Add(p - 1);
        }
    }
    return result.OrderBy(x => x).ToList();
}

static IResult PdfResult(byte[] data, string filename) =>
    Results.File(data, "application/pdf", filename);

static IResult ZipResult(byte[] data, string filename) =>
    Results.File(data, "application/zip", filename);

// ---------------------------------------------------------------------------
// Endpoints
// ---------------------------------------------------------------------------

// Health
app.MapGet("/health", () => new { status = "ok" }).WithTags("Système");

// 1. Info
app.MapPost("/info", async (IFormFile file) =>
{
    using var stream = file.OpenReadStream();
    var doc = OpenPdf(stream);
    var pages = new List<object>();
    for (int i = 0; i < doc.PageCount; i++)
    {
        var page = doc.Pages[i];
        double w = page.Width.Point;
        double h = page.Height.Point;
        pages.Add(new
        {
            page = i + 1,
            width_pt = Math.Round(w, 2),
            height_pt = Math.Round(h, 2),
            width_mm = Math.Round(w * 25.4 / 72, 1),
            height_mm = Math.Round(h * 25.4 / 72, 1),
            rotation = page.Rotate,
        });
    }
    return Results.Ok(new
    {
        filename = file.FileName,
        size_bytes = file.Length,
        page_count = doc.PageCount,
        pages,
    });
}).WithTags("Info").DisableAntiforgery();

// 2. Extract
app.MapPost("/split/extract", async ([FromForm] string pages, IFormFile file) =>
{
    using var stream = file.OpenReadStream();
    var doc = OpenPdf(stream);
    var indices = ParsePages(pages, doc.PageCount);
    if (indices.Count == 0) return Results.BadRequest(new { detail = "Aucune page sélectionnée." });
    return PdfResult(ExtractPages(doc, indices), "extrait.pdf");
}).WithTags("Découpe").DisableAntiforgery();

// 3. Split every N pages
app.MapPost("/split/every", async ([FromForm] int? n, IFormFile file) =>
{
    using var stream = file.OpenReadStream();
    var doc = OpenPdf(stream);
    int chunkSize = n ?? 1;
    if (chunkSize < 1) return Results.BadRequest(new { detail = "N doit être >= 1." });
    var stem = Path.GetFileNameWithoutExtension(file.FileName ?? "document");
    var files = new Dictionary<string, byte[]>();
    int part = 1;
    for (int start = 0; start < doc.PageCount; start += chunkSize)
    {
        int end = Math.Min(start + chunkSize, doc.PageCount);
        var indices = Enumerable.Range(start, end - start).ToList();
        files[$"{stem}_part{part:D3}.pdf"] = ExtractPages(doc, indices);
        part++;
    }
    if (files.Count == 1)
    {
        var (name, data) = files.First();
        return PdfResult(data, name);
    }
    return ZipResult(MakeZip(files), $"{stem}_split.zip");
}).WithTags("Découpe").DisableAntiforgery();

// 4. Split by ranges
app.MapPost("/split/ranges", async ([FromForm] string ranges, IFormFile file) =>
{
    using var stream = file.OpenReadStream();
    var doc = OpenPdf(stream);
    var stem = Path.GetFileNameWithoutExtension(file.FileName ?? "document");
    var specs = ranges.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (specs.Length == 0) return Results.BadRequest(new { detail = "Aucune plage spécifiée." });
    var files = new Dictionary<string, byte[]>();
    foreach (var spec in specs)
    {
        var indices = ParsePages(spec, doc.PageCount);
        if (indices.Count == 0) continue;
        int first = indices[0] + 1, last = indices[^1] + 1;
        files[$"{stem}_p{first}-{last}.pdf"] = ExtractPages(doc, indices);
    }
    if (files.Count == 0) return Results.BadRequest(new { detail = "Aucune page extraite." });
    if (files.Count == 1)
    {
        var (name, data) = files.First();
        return PdfResult(data, name);
    }
    return ZipResult(MakeZip(files), $"{stem}_ranges.zip");
}).WithTags("Découpe").DisableAntiforgery();

// 5. Split by size
app.MapPost("/split/size", async ([FromForm] double? maxMb, IFormFile file) =>
{
    using var stream = file.OpenReadStream();
    var doc = OpenPdf(stream);
    double limit = maxMb ?? 10.0;
    long maxBytes = (long)(limit * 1024 * 1024);
    var stem = Path.GetFileNameWithoutExtension(file.FileName ?? "document");
    var files = new Dictionary<string, byte[]>();
    int part = 1, startIdx = 0;
    while (startIdx < doc.PageCount)
    {
        int end = startIdx + 1;
        byte[] lastGood = ExtractPages(doc, new List<int> { startIdx });
        while (end < doc.PageCount)
        {
            var candidate = ExtractPages(doc, Enumerable.Range(startIdx, end - startIdx + 1).ToList());
            if (candidate.Length > maxBytes && end > startIdx) break;
            lastGood = candidate;
            end++;
        }
        files[$"{stem}_part{part:D3}_p{startIdx + 1}-{end}.pdf"] = lastGood;
        part++;
        startIdx = end;
    }
    if (files.Count == 1)
    {
        var (name, data) = files.First();
        return PdfResult(data, name);
    }
    return ZipResult(MakeZip(files), $"{stem}_sized.zip");
}).WithTags("Découpe").DisableAntiforgery();

// 6. Merge
app.MapPost("/merge", async (HttpRequest request) =>
{
    var form = await request.ReadFormAsync();
    var uploads = form.Files.GetFiles("files");
    if (uploads.Count < 2) return Results.BadRequest(new { detail = "Au moins 2 fichiers requis." });
    var merged = new PdfDocument();
    foreach (var f in uploads)
    {
        using var s = f.OpenReadStream();
        var src = OpenPdf(s);
        for (int i = 0; i < src.PageCount; i++)
            merged.AddPage(src.Pages[i]);
    }
    return PdfResult(WritePdf(merged), "merged.pdf");
}).WithTags("Fusion").DisableAntiforgery();

// 7. Rotate
app.MapPost("/rotate", async ([FromForm] string? pages, [FromForm] int? angle, IFormFile file) =>
{
    using var stream = file.OpenReadStream();
    var src = OpenPdf(stream);
    int rot = angle ?? 90;
    if (rot != 90 && rot != 180 && rot != 270)
        return Results.BadRequest(new { detail = "Angle doit être 90, 180 ou 270." });
    var pagesSpec = pages ?? "all";
    HashSet<int> indices;
    if (pagesSpec.Trim().ToLower() == "all")
        indices = Enumerable.Range(0, src.PageCount).ToHashSet();
    else
        indices = ParsePages(pagesSpec, src.PageCount).ToHashSet();
    var dst = new PdfDocument();
    for (int i = 0; i < src.PageCount; i++)
    {
        var page = dst.AddPage(src.Pages[i]);
        if (indices.Contains(i))
            page.Rotate = (page.Rotate + rot) % 360;
    }
    return PdfResult(WritePdf(dst), "rotated.pdf");
}).WithTags("Transformation").DisableAntiforgery();

// 8. Delete
app.MapPost("/delete", async ([FromForm] string pages, IFormFile file) =>
{
    using var stream = file.OpenReadStream();
    var doc = OpenPdf(stream);
    var toDelete = ParsePages(pages, doc.PageCount).ToHashSet();
    if (toDelete.Count >= doc.PageCount)
        return Results.BadRequest(new { detail = "Impossible de supprimer toutes les pages." });
    var toKeep = Enumerable.Range(0, doc.PageCount).Where(i => !toDelete.Contains(i)).ToList();
    return PdfResult(ExtractPages(doc, toKeep), "trimmed.pdf");
}).WithTags("Transformation").DisableAntiforgery();

// 9. Reorder
app.MapPost("/reorder", async ([FromForm] string order, IFormFile file) =>
{
    using var stream = file.OpenReadStream();
    var doc = OpenPdf(stream);
    List<int> indices;
    try
    {
        indices = order.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.Parse(x) - 1).ToList();
    }
    catch { return Results.BadRequest(new { detail = "Format d'ordre invalide." }); }
    foreach (var i in indices)
        if (i < 0 || i >= doc.PageCount)
            return Results.BadRequest(new { detail = $"Page {i + 1} hors limites (1-{doc.PageCount})." });
    return PdfResult(ExtractPages(doc, indices), "reordered.pdf");
}).WithTags("Transformation").DisableAntiforgery();

app.Run();
