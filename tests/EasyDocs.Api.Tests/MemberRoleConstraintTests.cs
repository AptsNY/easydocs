using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EasyDocs.Api.Tests;

// The role columns hold enum NAMES, and both server-side authorization and the SPA's Actions menu
// compare those strings. A bulk import once wrote the enum ORDINALS instead ("0", "1") — valid rows
// as far as Postgres was concerned, silently unrecognisable everywhere else. The CK_*_Role
// constraints exist so that write dies at the database; an undefined enum value is the EF-visible
// way to produce exactly that malformed string.
public class MemberRoleConstraintTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task a_role_that_is_not_a_known_name_is_rejected_by_the_database()
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();

        var org = new Organization { Id = Guid.NewGuid(), Slug = $"ck-{Guid.NewGuid():N}", Name = "ck", CreatedAt = DateTimeOffset.UtcNow };
        var user = new User { Id = Guid.NewGuid(), Email = $"ck-{Guid.NewGuid():N}@example.com", DisplayName = "ck", CreatedAt = DateTimeOffset.UtcNow };
        db.AddRange(org, user);
        await db.SaveChangesAsync();

        // (OrgRole)7 has no name, so the string conversion stores "7" — the same shape as the
        // ordinal-writing bug. The constraint, not application code, must refuse it.
        db.Add(new OrgMember { OrgId = org.Id, UserId = user.Id, Role = (OrgRole)7, CreatedAt = DateTimeOffset.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
