using EasyDocs.Api.Data;
using EasyDocs.Api.Diffing;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Storage;
using EasyDocs.Api.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasyDocs.Api.Tests;

// The redline half of the version_diffs insert race, made deterministic. The losing interleaving is
// tight: RedlineHtmlAsync's upsert re-queries and finds nothing, and the summary worker's insert
// lands in the gap between that query and SaveChanges — so the redline's insert dies on the
// composite PK. Before the fix, the catch dropped the freshly computed html pointers: the response
// was a 200 with html, the cache silently never filled, and DiffTests flaked in CI with
// Assert.NotNull(HtmlBlobSha256) — on a docs-only commit, naturally.
//
// A SaveChangesInterceptor on the service's OWN context reproduces that exact gap: the moment the
// service is about to insert its VersionDiff row, the "worker" inserts the conflicting one first.
public class DiffRedlineRaceTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private sealed class ConflictInjector(Func<Task> insertConflict) : SaveChangesInterceptor
    {
        private int _fired;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            var addingDiff = eventData.Context!.ChangeTracker.Entries<VersionDiff>()
                .Any(e => e.State == EntityState.Added);
            if (addingDiff && Interlocked.Exchange(ref _fired, 1) == 0)
                await insertConflict();
            return result;
        }
    }

    [Fact]
    public async Task A_lost_insert_race_still_fills_the_html_cache()
    {
        var store = f.Services.GetRequiredService<IBlobStore>();
        var (fromBytes, toBytes) = DocxFixtures.UniquePair();
        var from = await store.PutAsync(new MemoryStream(fromBytes));
        var to = await store.PutAsync(new MemoryStream(toBytes));

        using var seedScope = f.Services.CreateScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        foreach (var b in new[] { (from, fromBytes), (to, toBytes) })
            if (!await seedDb.Blobs.AnyAsync(x => x.Sha256 == b.Item1.Sha256))
                seedDb.Add(new Blob { Sha256 = b.Item1.Sha256, SizeBytes = b.Item1.SizeBytes,
                    Mime = "application/octet-stream", StorageKey = b.Item1.Sha256, CreatedAt = DateTimeOffset.UtcNow });
        await seedDb.SaveChangesAsync();

        // The "summary worker": wins the insert with null html pointers, exactly inside the gap.
        async Task InsertSummaryRowAsync()
        {
            using var s = f.Services.CreateScope();
            var raceDb = s.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            raceDb.Add(new VersionDiff
            {
                FromSha256 = from.Sha256, ToSha256 = to.Sha256,
                Insertions = 1, Deletions = 0, Moves = 0, FormatChanges = 0,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await raceDb.SaveChangesAsync();
        }

        var options = new DbContextOptionsBuilder<EasyDocsDbContext>()
            .UseNpgsql(seedDb.Database.GetConnectionString())
            .AddInterceptors(new ConflictInjector(InsertSummaryRowAsync))
            .Options;
        await using var racingDb = new EasyDocsDbContext(options);
        var service = new WmlComparerDiffService(store, racingDb, NullLogger<WmlComparerDiffService>.Instance);

        var render = await service.RedlineHtmlAsync(from.Sha256, to.Sha256, CancellationToken.None);
        Assert.True(render.Available);
        Assert.NotNull(render.Html);

        using var verify = f.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var row = await verifyDb.VersionDiffs.SingleAsync(
            d => d.FromSha256 == from.Sha256 && d.ToSha256 == to.Sha256);
        Assert.NotNull(row.HtmlBlobSha256);   // the loser must update the winner's row
        Assert.NotNull(row.RedlineBlobSha256);
        Assert.Equal(1, row.Insertions);       // and must not clobber the winner's summary
    }
}
