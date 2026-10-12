using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Kernel.Security;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class TemporaryDelegationTests(PostgresFixture postgres)
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}");
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static async Task Rejected(SlittingPlant.Plant plant, HttpClient actor, string path, object body, HttpStatusCode status, string code)
    {
        var response = await plant.Try(actor, path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(status, response.StatusCode);
        Assert.Contains(code, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approved_delegation_is_scoped_scheduled_and_expiring_without_changing_base_roles()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services => services.AddSingleton<TimeProvider>(clock));
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("delegation-programs", "ProgramManager");
        var programs = plant.As("delegation-programs");
        var programA = Guid.Parse((await plant.Ok(programs, "/api/programs/", new { code = "DELEGATION-A", name = "Delegation plant scope A", reason = "Delegation acceptance" })).GetProperty("entityId").GetString()!);
        var programB = Guid.Parse((await plant.Ok(programs, "/api/programs/", new { code = "DELEGATION-B", name = "Delegation plant scope B", reason = "Delegation acceptance" })).GetProperty("entityId").GetString()!);

        var grantorId = await plant.Person("delegation-grantor", "SecurityAdministrator");
        var recipientId = await plant.Person("delegation-recipient", "Auditor", "SecurityAdministrator");
        var outsiderId = await plant.Person("delegation-outsider", "Auditor");
        foreach (var person in new[] { grantorId, recipientId, plant.People["eng"] })
        foreach (var program in new[] { programA, programB })
            await plant.Ok(plant.Admin, $"/api/kernel/people/{person}/programs", new { programId = program, reason = "Independent program membership" });
        await plant.Ok(plant.Admin, "/api/kernel/roles", new { role = "DelegationOrderAuthor", displayName = "Scoped order author", permissions = new[] { Permissions.OrdersManage }, reason = "Configure delegation source authority" });
        await plant.Ok(plant.Admin, "/api/kernel/roles/DelegationOrderAuthor/permission-scopes", new { permission = Permissions.OrdersManage, scopeType = "program", scopeId = programA.ToString("D"), reason = "Only program A" });
        await plant.Ok(plant.Admin, $"/api/kernel/people/{grantorId}/roles", new { role = "DelegationOrderAuthor", reason = "Grantor's own base role" });
        var grantor = plant.As("delegation-grantor");
        var recipient = plant.As("delegation-recipient");
        var outsider = plant.As("delegation-outsider");
        var initialRoles = (await Json(await recipient.GetAsync("/api/kernel/me"))).GetProperty("roles").EnumerateArray().Select(role => role.GetString()).ToArray();
        var scopes = new[] { new { scopeType = "program", scopeId = programA.ToString("D") } };
        object Proposal(DateTimeOffset start, DateTimeOffset end, string[]? permissions = null, Guid? target = null, object[]? bounds = null, string? reason = "Cover program A during the planning shift") =>
            new { recipient = target ?? recipientId, permissions = permissions ?? [Permissions.OrdersManage], scopes = bounds ?? scopes.Cast<object>().ToArray(), startsAt = start, expiresAt = end, reason };
        object Order(Guid? program) => new { partNumber = "ROLL-100", quantity = 10m, dueDate = clock.GetUtcNow().AddDays(20).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), priority = 3, programId = program, reason = "Exercise delegated order creation" };
        var start = clock.GetUtcNow().AddHours(1);
        var end = start.AddHours(1);
        await Rejected(plant, grantor, "/api/kernel/delegations", Proposal(start, end, reason: null), HttpStatusCode.UnprocessableEntity, "reason_required");

        var proposal = Proposal(start, end);
        var created = await Json(await grantor.CommandAsync("/api/kernel/delegations", proposal, "delegation-propose-once"));
        var replay = await Json(await grantor.CommandAsync("/api/kernel/delegations", proposal, "delegation-propose-once"));
        var id = Guid.Parse(created.GetProperty("entityId").GetString()!);
        Assert.Equal(id.ToString("D"), replay.GetProperty("entityId").GetString());
        var pending = (await Json(await grantor.GetAsync("/api/kernel/delegations"))).EnumerateArray().Single(record => record.GetProperty("delegationId").GetGuid() == id);
        Assert.Equal("Pending approval", pending.GetProperty("state").GetString());
        Assert.Equal((grantorId, recipientId), (pending.GetProperty("grantor").GetGuid(), pending.GetProperty("recipient").GetGuid()));
        await Rejected(plant, recipient, "/api/orders/", Order(programA), HttpStatusCode.Forbidden, Permissions.OrdersManage);
        await Rejected(plant, grantor, $"/api/kernel/delegations/{id}/approve", new { reason = "Self approval is prohibited" }, HttpStatusCode.UnprocessableEntity, "delegation_approval_separation");
        await Rejected(plant, recipient, $"/api/kernel/delegations/{id}/approve", new { reason = "Recipient approval is prohibited" }, HttpStatusCode.UnprocessableEntity, "delegation_approval_separation");
        await Rejected(plant, outsider, $"/api/kernel/delegations/{id}/approve", new { reason = "No approval authority" }, HttpStatusCode.Forbidden, Permissions.AccessManage);
        Assert.Empty((await Json(await outsider.GetAsync("/api/kernel/delegations"))).EnumerateArray());
        await plant.Ok(plant.Admin, $"/api/kernel/delegations/{id}/approve", new { reason = "Independent approval of the bounded shift cover" });
        var scheduled = (await Json(await recipient.GetAsync("/api/kernel/delegations"))).EnumerateArray().Single(record => record.GetProperty("delegationId").GetGuid() == id);
        Assert.Equal("Scheduled", scheduled.GetProperty("state").GetString());
        await Rejected(plant, recipient, "/api/orders/", Order(programA), HttpStatusCode.Forbidden, Permissions.OrdersManage);

        clock.Advance(TimeSpan.FromHours(1));
        await plant.Ok(recipient, "/api/orders/", Order(programA));
        await Rejected(plant, recipient, "/api/orders/", Order(programB), HttpStatusCode.Forbidden, Permissions.OrdersManage);
        await Rejected(plant, recipient, "/api/orders/", Order(null), HttpStatusCode.Forbidden, Permissions.OrdersManage);
        var activeMe = await Json(await recipient.GetAsync("/api/kernel/me"));
        var cachedPrincipal = await app.Services.GetRequiredService<KernelReadStore>().ResolvePrincipalBySubjectAsync("delegation-recipient", CancellationToken.None);
        Assert.NotNull(cachedPrincipal);
        Assert.Equal(initialRoles, activeMe.GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
        var effective = Assert.Single(activeMe.GetProperty("delegatedPermissions").EnumerateArray());
        Assert.Equal((id, Permissions.OrdersManage), (effective.GetProperty("delegationId").GetGuid(), effective.GetProperty("permission").GetString()));
        await plant.Ok(plant.Admin, $"/api/kernel/people/{recipientId}/programs/{programA}/revoke", new { reason = "Program membership remains an independent boundary" });
        await Rejected(plant, recipient, "/api/orders/", Order(programA), HttpStatusCode.Forbidden, "No access to the program");
        await plant.Ok(plant.Admin, $"/api/kernel/people/{recipientId}/programs", new { programId = programA, reason = "Restore independently approved membership" });
        await plant.Ok(plant.Admin, $"/api/kernel/people/{grantorId}/programs/{programA}/revoke", new { reason = "Grantor must retain authority in the program" });
        await Rejected(plant, recipient, "/api/orders/", Order(programA), HttpStatusCode.Forbidden, Permissions.OrdersManage);
        Assert.Empty((await Json(await recipient.GetAsync("/api/kernel/me"))).GetProperty("delegatedPermissions").EnumerateArray());
        await plant.Ok(plant.Admin, $"/api/kernel/people/{grantorId}/programs", new { programId = programA, reason = "Restore the source program boundary" });
        await Rejected(plant, recipient, "/api/kernel/delegations", Proposal(clock.GetUtcNow(), end, target: outsiderId), HttpStatusCode.UnprocessableEntity, "delegation_escalation");
        await Rejected(plant, grantor, "/api/kernel/delegations", Proposal(clock.GetUtcNow(), end, permissions: [Permissions.OrdersManage, Permissions.ToolsManage]), HttpStatusCode.UnprocessableEntity, "delegation_escalation");
        await Rejected(plant, grantor, "/api/kernel/delegations", Proposal(clock.GetUtcNow(), end, permissions: [Permissions.AccessManage]), HttpStatusCode.UnprocessableEntity, "delegation_escalation");
        await Rejected(plant, grantor, "/api/kernel/delegations", Proposal(clock.GetUtcNow(), end, bounds: [new { scopeType = "program", scopeId = programB.ToString("D") }]), HttpStatusCode.UnprocessableEntity, "delegation_escalation");
        await Rejected(plant, grantor, "/api/kernel/delegations", Proposal(clock.GetUtcNow(), end, bounds: []), HttpStatusCode.UnprocessableEntity, "delegation_escalation");
        await Rejected(plant, grantor, "/api/kernel/delegations", Proposal(end, clock.GetUtcNow()), HttpStatusCode.UnprocessableEntity, "invalid_delegation_interval");

        clock.Advance(TimeSpan.FromHours(1));
        Assert.False(app.Services.GetRequiredService<IForgeAuthorizer>().Authorize(cachedPrincipal, Permissions.OrdersManage,
            new ResourceRef("ProductionOrder", Program: new Forge.Kernel.Identity.ProgramId(programA))).IsPermitted);
        await Rejected(plant, recipient, "/api/orders/", Order(programA), HttpStatusCode.Forbidden, Permissions.OrdersManage);
        var expiredMe = await Json(await recipient.GetAsync("/api/kernel/me"));
        Assert.Empty(expiredMe.GetProperty("delegatedPermissions").EnumerateArray());
        Assert.Equal(initialRoles, expiredMe.GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
        var expired = (await Json(await recipient.GetAsync("/api/kernel/delegations"))).EnumerateArray().Single(record => record.GetProperty("delegationId").GetGuid() == id);
        Assert.Equal("Expired", expired.GetProperty("state").GetString());
        var audit = await Json(await plant.Admin.GetAsync($"/api/kernel/audit?entityType=AuthorityDelegation&entityId={id}"));
        Assert.Equal(2, audit.EnumerateArray().Count(entry => entry.GetProperty("entry").GetProperty("outcome").GetString() == "Succeeded"));
        Assert.Contains(audit.EnumerateArray(), entry => entry.GetProperty("entry").GetProperty("reason").GetString() == "Cover program A during the planning shift");
        Assert.Contains(audit.EnumerateArray(), entry => entry.GetProperty("entry").GetProperty("reason").GetString() == "Independent approval of the bounded shift cover");
        Assert.True((await Json(await plant.Admin.GetAsync("/api/kernel/audit/verify"))).GetProperty("intact").GetBoolean());

        // A fresh approved interval stops granting immediately when its source is revoked, and can itself be revoked.
        var renewed = Guid.Parse((await plant.Ok(grantor, "/api/kernel/delegations", Proposal(clock.GetUtcNow(), clock.GetUtcNow().AddHours(1)))).GetProperty("entityId").GetString()!);
        await plant.Ok(plant.Admin, $"/api/kernel/delegations/{renewed}/approve", new { reason = "Second bounded cover" });
        await plant.Ok(recipient, "/api/orders/", Order(programA));
        await plant.Ok(plant.Admin, "/api/kernel/roles/DelegationOrderAuthor/permissions/execution.orders.manage/revoke", new { reason = "Revoke source authority" });
        await Rejected(plant, recipient, "/api/orders/", Order(programA), HttpStatusCode.Forbidden, Permissions.OrdersManage);
        await plant.Ok(plant.Admin, $"/api/kernel/delegations/{renewed}/revoke", new { reason = "Withdraw the delegation record" });
        var revoked = (await Json(await recipient.GetAsync("/api/kernel/delegations"))).EnumerateArray().Single(record => record.GetProperty("delegationId").GetGuid() == renewed);
        Assert.Equal("Revoked", revoked.GetProperty("state").GetString());
        Assert.Equal(initialRoles, (await Json(await recipient.GetAsync("/api/kernel/me"))).GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
    }

    [Fact]
    public async Task Borrowed_project_approval_retains_source_amount_currency_and_current_policy_limits()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services => services.AddSingleton<TimeProvider>(clock));
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("delegation-facilities", "FacilitiesManager");
        var recipientId = await plant.Person("delegation-budget-recipient", "Auditor");
        var grantor = plant.As("delegation-facilities");
        var recipient = plant.As("delegation-budget-recipient");
        await plant.Ok(plant.Admin, $"/api/kernel/approval-thresholds/{Permissions.ProjectsApprove}", new { maximumAmount = 1000m, currency = "USD", reason = "Source approval limit" });
        var created = await plant.Ok(grantor, "/api/kernel/delegations", new
        {
            recipient = recipientId, permissions = new[] { Permissions.ProjectsApprove }, scopes = Array.Empty<object>(),
            startsAt = clock.GetUtcNow(), expiresAt = clock.GetUtcNow().AddHours(1), reason = "Cover approvals within the source USD limit",
        });
        var delegation = Guid.Parse(created.GetProperty("entityId").GetString()!);
        await plant.Ok(plant.Admin, $"/api/kernel/delegations/{delegation}/approve", new { reason = "Approve the amount-bounded cover" });
        var me = await Json(await recipient.GetAsync("/api/kernel/me"));
        var source = Assert.Single(me.GetProperty("delegatedPermissions").EnumerateArray()).GetProperty("sourceThreshold");
        Assert.Equal((1000m, "USD"), (source.GetProperty("maximumAmount").GetDecimal(), source.GetProperty("currency").GetString()));

        var borrower = await app.Services.GetRequiredService<KernelReadStore>().ResolvePrincipalBySubjectAsync("delegation-budget-recipient", CancellationToken.None);
        Assert.NotNull(borrower);
        // Source limits remain even if a future person-specific policy gives the borrower no own threshold.
        var sourceOnly = borrower with { ApprovalThresholds = [] };
        var authorizer = app.Services.GetRequiredService<IForgeAuthorizer>();
        Assert.True(authorizer.Authorize(sourceOnly, Permissions.ProjectsApprove, new ResourceRef("Project", Amount: 1000m, Currency: "USD")).IsPermitted);
        Assert.False(authorizer.Authorize(sourceOnly, Permissions.ProjectsApprove, new ResourceRef("Project", Amount: 1001m, Currency: "USD")).IsPermitted);
        Assert.False(authorizer.Authorize(sourceOnly, Permissions.ProjectsApprove, new ResourceRef("Project", Amount: 1000m, Currency: "EUR")).IsPermitted);

        var start = clock.GetUtcNow().AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = clock.GetUtcNow().AddDays(30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var (code, budget) in new[] { ("DELEGATION-1000", 1000m), ("DELEGATION-1001", 1001m), ("DELEGATION-950", 950m), ("DELEGATION-900", 900m) })
            await plant.Ok(plant.Engineer, "/api/projects/", new { code, title = "Delegated facilities approval", type = "Facility", funding = "Operating", budget, startDate = start, endDate = end });
        await plant.Ok(recipient, "/api/projects/DELEGATION-1000/approve", new { reason = "Exactly within the inherited limit" });
        await Rejected(plant, recipient, "/api/projects/DELEGATION-1001/approve", new { reason = "Cannot exceed source authority" }, HttpStatusCode.Forbidden, "approval threshold");
        var change = await plant.Ok(plant.Engineer, "/api/projects/DELEGATION-1000", new { title = "Delegated facilities approval", budget = 1001m, startDate = start, endDate = end, reason = "A budget increase still requires bounded approval" });
        await Rejected(plant, recipient, $"/api/projects/changes/{change.GetProperty("entityId").GetString()}/approve", new { reason = "Cannot bypass the limit through a change" }, HttpStatusCode.Forbidden, "approval threshold");
        Assert.Equal(1000m, (await Json(await recipient.GetAsync("/api/projects/DELEGATION-1000"))).GetProperty("project").GetProperty("budget").GetDecimal());
        await plant.Ok(plant.Admin, $"/api/kernel/approval-thresholds/{Permissions.ProjectsApprove}", new { maximumAmount = 900m, currency = "USD", reason = "The current limit tightened" });
        await Rejected(plant, recipient, "/api/projects/DELEGATION-950/approve", new { reason = "A previous delegation cannot preserve a withdrawn limit" }, HttpStatusCode.Forbidden, "approval threshold");
        Assert.Equal("Draft", (await Json(await recipient.GetAsync("/api/projects/DELEGATION-950"))).GetProperty("project").GetProperty("status").GetString());
        await Rejected(plant, recipient, "/api/projects/DELEGATION-950/cancel", new { reason = "Cancellation uses the actual affected budget too" }, HttpStatusCode.Forbidden, "approval threshold");
        await plant.Ok(recipient, "/api/projects/DELEGATION-900/cancel", new { reason = "Cancel the draft exactly within current inherited authority" });
        Assert.Equal("Cancelled", (await Json(await recipient.GetAsync("/api/projects/DELEGATION-900"))).GetProperty("project").GetProperty("status").GetString());
    }
}
