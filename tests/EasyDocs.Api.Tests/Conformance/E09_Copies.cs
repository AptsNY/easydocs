using System.Net;
using System.Net.Http.Json;

namespace EasyDocs.Api.Tests.Conformance;

// E9 Copies (spec §12.1): isolated members/history; non-member push -> pending review; accept ->
// incoming branch; reject -> hidden + pusher notified; merge into main via fork-point ancestor.
//
// PENDING UNTIL M4 by design (spec §13: M3 ships the public API before copies & push). These skip with
// a reason rather than failing, so the suite stays honest about what is and is not covered — a green
// run today means "E1-E8, E10-E12 pass and E9 is not yet built", never "E9 passes".
[Collection(ConformanceCollection.Name)]
public class E09_Copies
{
    private const string PendingM4 = "Copies & push ship in M4 (spec §13); E9 is expected to be unimplemented in M3.";

    private readonly ApiFactory _f;
    public E09_Copies(ApiFactory f) => _f = f;

    [SkippableFact]
    public void Copy_has_isolated_members_and_history() => Skip.If(true, PendingM4);

    [SkippableFact]
    public void Non_member_push_creates_a_pending_review() => Skip.If(true, PendingM4);

    [SkippableFact]
    public void Accepting_a_push_lands_it_on_an_incoming_branch() => Skip.If(true, PendingM4);

    [SkippableFact]
    public void Rejecting_a_push_hides_it_and_notifies_the_pusher() => Skip.If(true, PendingM4);

    [SkippableFact]
    public void Push_merges_into_main_via_the_fork_point_ancestor() => Skip.If(true, PendingM4);

    [SkippableFact]
    public void A_copy_never_leaks_master_drafts() => Skip.If(true, PendingM4);
}
