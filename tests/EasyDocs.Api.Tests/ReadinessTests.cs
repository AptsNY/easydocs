using System.Net;

namespace EasyDocs.Api.Tests;

// /health is the shallow liveness probe (the platform must not kill the instance during a DB outage);
// /health/ready is the deploy smoke check and must go red when Postgres is unreachable. The 2026-08
// credentials-rotation outage shipped green on /health, and the Cloud Run deploy workflow smokes
// /health/ready through the load balancer — so the split has to be asserted, not assumed.
//
// Own ApiFactory (IClassFixture is per class): stopping its Postgres container affects no other test.
public class ReadinessTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Ready_is_200_with_a_database_and_503_without_one_while_health_stays_200()
    {
        var client = f.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);

        await f.StopDatabaseAsync();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
    }
}
