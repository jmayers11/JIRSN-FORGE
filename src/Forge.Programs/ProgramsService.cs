using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Programs.Contracts;

namespace Forge.Programs;

public sealed record CreateProgram(string Code, string Name, string? Customer, string? Description);

public sealed record UpdateProgram(Guid ProgramId, string Name, string? Customer, string? Description);

public sealed record SetProgramStatus(Guid ProgramId, ProgramStatus Status);

public sealed record AddContract(Guid ProgramId, string Number, string Customer, string Title, DateOnly PeriodStart, DateOnly PeriodEnd, decimal? Ceiling);

/// <summary>A contract modification: new title, period of performance or ceiling.</summary>
public sealed record ModifyContract(long ContractId, string Title, DateOnly PeriodStart, DateOnly PeriodEnd, decimal? Ceiling);

public sealed record SetContractStatus(long ContractId, ContractStatus Status);

public sealed record AddClin(long ContractId, string Clin, string? ParentClin, string Description, string? PartNumber, decimal? Quantity, decimal? UnitPrice);

public sealed record ScheduleDelivery(long ContractId, string Clin, decimal Quantity, DateOnly DueDate);

public sealed record ChangeDelivery(long DeliveryId, decimal Quantity, DateOnly DueDate);

public sealed record CancelDelivery(long DeliveryId);

public sealed record AddRequirement(long ContractId, string Code, string Text);

public sealed record AddMilestone(Guid ProgramId, string Title, DateOnly DueDate);

public sealed record CompleteMilestone(long MilestoneId, DateOnly CompletedOn);

/// <summary>Funds obligated (or, negative, de-obligated) by a contract action such as a modification.</summary>
public sealed record AddFunding(long ContractId, string? Clin, string Modification, decimal Amount, DateOnly ObligatedOn);

public sealed record AddWbsElement(Guid ProgramId, string Code, string? ParentCode, string Title);

public sealed record ProgramChanged(Guid ProgramId, string Kind, string Id, string Change) : IDomainEvent
{
    public static string EventType => nameof(ProgramChanged);

    public static int SchemaVersion => 1;
}

public interface IProgramsStore
{
    Task<bool> InsertProgramAsync(ProgramInfo program, CancellationToken ct);

    Task UpdateProgramAsync(ProgramInfo program, CancellationToken ct);

    Task<long?> InsertContractAsync(ContractInfo contract, DateTimeOffset at, string actor, CancellationToken ct);

    Task UpdateContractAsync(ContractInfo contract, CancellationToken ct);

    Task<bool> InsertClinAsync(long contractId, ClinInfo clin, CancellationToken ct);

    Task<long> InsertDeliveryAsync(DeliveryInfo delivery, CancellationToken ct);

    Task UpdateDeliveryAsync(DeliveryInfo delivery, CancellationToken ct);

    Task InsertRequirementAsync(long contractId, RequirementInfo requirement, CancellationToken ct);

    Task<long> InsertMilestoneAsync(MilestoneInfo milestone, DateTimeOffset at, string actor, CancellationToken ct);

    Task<MilestoneInfo?> FindMilestoneAsync(long milestoneId, CancellationToken ct);

    Task<long> InsertFundingAsync(FundingInfo funding, CancellationToken ct);

    Task<bool> InsertWbsAsync(WbsElement element, DateTimeOffset at, string actor, CancellationToken ct);

    Task UpdateMilestoneAsync(MilestoneInfo milestone, CancellationToken ct);
}

/// <summary>
/// Programs and contracts (spec §7, §72). A program owns contracts; a contract owns CLINs (and SLINs
/// under them), a delivery schedule and its requirements. Planning turns deliveries into demand. Every
/// program-scoped record is visible only to people with access to that program; others are told it
/// does not exist. Contract modifications and delivery changes need a reason.
/// </summary>
public sealed class ProgramsService(CommandPipeline pipeline, TimeProvider time)
{
    public const string SourceContext = "Programs";
    private const string ProgramEntity = "Program";
    private const string ContractEntity = "Contract";

    public Task<CommandResult> CreateProgramAsync(CreateProgram c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.Code, "Program");
        var spec = new CommandSpec(SourceContext, nameof(CreateProgram), Permissions.ProgramsManage, ResourceRef.Of(ProgramEntity, code));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var program = new ProgramInfo(ForgeIds.New(time), code, BusinessCode.RequireName(c.Name, "Program"), Trim(c.Customer, 200), Trim(c.Description, 2000), ProgramStatus.Active, context.Actor, now);
            if (!await uow.Store<IProgramsStore>().InsertProgramAsync(program, ct))
            {
                throw new CommandRejectedException("code_exists", $"Program {code} already exists.");
            }

            var pid = new ProgramId(program.ProgramId);
            var id = program.ProgramId.ToString("D", CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(id, $"Program {code} created. A security administrator grants people access to it."),
                [pipeline.Event(new ProgramChanged(program.ProgramId, ProgramEntity, id, "Created"), context, SourceContext, ProgramEntity, id, pid)],
                new AuditChange(ProgramEntity, id, null, CanonicalJson.Serialize(program), pid));
        }, ct);
    }

    public Task<CommandResult> UpdateProgramAsync(UpdateProgram c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ProgramAsync(nameof(UpdateProgram), c.ProgramId, c, context, false, p =>
            (p with { Name = BusinessCode.RequireName(c.Name, "Program"), Customer = Trim(c.Customer, 200), Description = Trim(c.Description, 2000) }, "updated"), ct);
    }

    public Task<CommandResult> SetProgramStatusAsync(SetProgramStatus c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ProgramAsync(nameof(SetProgramStatus), c.ProgramId, c, context, true, p => (p with { Status = c.Status }, c.Status == ProgramStatus.Closed ? "closed" : "reopened"), ct);
    }

    public Task<CommandResult> AddContractAsync(AddContract c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var pid = new ProgramId(c.ProgramId);
        var spec = new CommandSpec(SourceContext, nameof(AddContract), Permissions.ProgramsManage, new ResourceRef(ContractEntity, null, pid));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var program = await ActiveProgramAsync(uow, c.ProgramId, context, ct);
            var number = BusinessCode.Require(c.Number, "Contract");
            ValidatePeriod(c.PeriodStart, c.PeriodEnd, c.Ceiling);
            var contract = new ContractInfo(0, program.ProgramId, number, BusinessCode.RequireName(c.Customer, "Customer"), BusinessCode.RequireName(c.Title, "Contract"),
                c.PeriodStart, c.PeriodEnd, c.Ceiling, ContractStatus.Active, [], [], [], []);
            var contractId = await uow.Store<IProgramsStore>().InsertContractAsync(contract, now, context.Actor, ct)
                ?? throw new CommandRejectedException("number_exists", $"Contract {number} already exists.");
            return Outcome(context, program.ProgramId, ContractEntity, Key(contractId), "Created", $"Contract {number} added to {program.Code}.", null, contract with { ContractId = contractId });
        }, ct);
    }

    public Task<CommandResult> ModifyContractAsync(ModifyContract c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ContractAsync(nameof(ModifyContract), c.ContractId, c, context, true, async (uow, contract, _) =>
        {
            ValidatePeriod(c.PeriodStart, c.PeriodEnd, c.Ceiling);
            var late = contract.Deliveries.Where(d => !d.Cancelled && d.DueDate > c.PeriodEnd).ToList();
            if (late.Count > 0)
            {
                throw new CommandRejectedException("deliveries_outside_period", $"Deliveries {string.Join(", ", late.Select(d => d.DeliveryId))} fall after {c.PeriodEnd:yyyy-MM-dd}. Move them first.");
            }

            var next = contract with { Title = BusinessCode.RequireName(c.Title, "Contract"), PeriodStart = c.PeriodStart, PeriodEnd = c.PeriodEnd, Ceiling = c.Ceiling };
            await uow.Store<IProgramsStore>().UpdateContractAsync(next, ct);
            return (next, "modified");
        }, ct);
    }

    public Task<CommandResult> SetContractStatusAsync(SetContractStatus c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ContractAsync(nameof(SetContractStatus), c.ContractId, c, context, true, async (uow, contract, _) =>
        {
            var next = contract with { Status = c.Status };
            await uow.Store<IProgramsStore>().UpdateContractAsync(next, ct);
            return (next, c.Status.ToString().ToLowerInvariant());
        }, ct, requireActive: false);
    }

    public Task<CommandResult> AddClinAsync(AddClin c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ContractAsync(nameof(AddClin), c.ContractId, c, context, false, async (uow, contract, _) =>
        {
            var clin = ClinCode(c.Clin);
            var parent = string.IsNullOrWhiteSpace(c.ParentClin) ? null : ClinCode(c.ParentClin);
            if (parent is not null && contract.Clins.All(x => x.Clin != parent))
            {
                throw new CommandRejectedException("unknown_clin", $"Contract {contract.Number} has no CLIN {parent}.");
            }

            string? part = null;
            if (!string.IsNullOrWhiteSpace(c.PartNumber))
            {
                part = BusinessCode.Require(c.PartNumber, "Part");
                if (await uow.Store<IEngineeringLookup>().FindItemAsync(part, ct) is null)
                {
                    throw new CommandRejectedException("unknown_part", $"Part {part} does not exist.");
                }
            }

            if (c.Quantity is <= 0 || c.UnitPrice is < 0)
            {
                throw new CommandRejectedException("invalid_quantity", "Quantity must be above zero and price not negative.");
            }

            var info = new ClinInfo(clin, parent, BusinessCode.RequireName(c.Description, "CLIN"), part, c.Quantity, c.UnitPrice);
            if (!await uow.Store<IProgramsStore>().InsertClinAsync(contract.ContractId, info, ct))
            {
                throw new CommandRejectedException("clin_exists", $"Contract {contract.Number} already has {(parent is null ? "CLIN" : "SLIN")} {clin}.");
            }

            return (contract with { Clins = [.. contract.Clins, info] }, $"{(parent is null ? "CLIN" : "SLIN")} {clin} added");
        }, ct);
    }

    public Task<CommandResult> ScheduleDeliveryAsync(ScheduleDelivery c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(ScheduleDelivery), Permissions.ProgramsManage, ResourceRef.Of("Delivery"));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var contract = await VisibleContractAsync(uow, c.ContractId, context, ct);
            if (contract.Status != ContractStatus.Active)
            {
                throw new CommandRejectedException("contract_not_active", $"Contract {contract.Number} is {contract.Status}.");
            }

            var clin = ClinCode(c.Clin);
            var line = contract.Clins.FirstOrDefault(x => x.Clin == clin)
                ?? throw new CommandRejectedException("unknown_clin", $"Contract {contract.Number} has no CLIN {clin}.");
            CheckDelivery(contract, line, null, c.Quantity, c.DueDate);
            var delivery = new DeliveryInfo(0, contract.ContractId, clin, c.Quantity, c.DueDate, false, context.Actor, now);
            var deliveryId = await uow.Store<IProgramsStore>().InsertDeliveryAsync(delivery, ct);

            // The result names the new delivery, which is what a planner turns into demand.
            return Outcome(context, contract.ProgramId, "Delivery", Key(deliveryId), "Scheduled",
                $"Delivery {deliveryId}: {c.Quantity} {line.PartNumber} due {c.DueDate:yyyy-MM-dd} on {contract.Number} CLIN {clin}. A planner turns it into demand.",
                null, delivery with { DeliveryId = deliveryId });
        }, ct);
    }

    public Task<CommandResult> ChangeDeliveryAsync(ChangeDelivery c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return DeliveryAsync(nameof(ChangeDelivery), c.DeliveryId, c, context, (contract, delivery) =>
        {
            var line = contract.Clins.First(x => x.Clin == delivery.Clin);
            CheckDelivery(contract, line, delivery.DeliveryId, c.Quantity, c.DueDate);
            return (delivery with { Quantity = c.Quantity, DueDate = c.DueDate }, $"now {c.Quantity} due {c.DueDate:yyyy-MM-dd}; planned demand shows as out of date until a planner updates it");
        }, ct);
    }

    public Task<CommandResult> CancelDeliveryAsync(CancelDelivery c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return DeliveryAsync(nameof(CancelDelivery), c.DeliveryId, c, context, (_, delivery) => (delivery with { Cancelled = true }, "cancelled"), ct);
    }

    public Task<CommandResult> AddRequirementAsync(AddRequirement c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ContractAsync(nameof(AddRequirement), c.ContractId, c, context, false, async (uow, contract, _) =>
        {
            var requirement = new RequirementInfo(contract.Requirements.Count == 0 ? 1 : contract.Requirements.Max(r => r.Number) + 1, BusinessCode.RequireName(c.Code, "Requirement"), BusinessCode.RequireName(c.Text, "Requirement text"));
            await uow.Store<IProgramsStore>().InsertRequirementAsync(contract.ContractId, requirement, ct);
            return (contract with { Requirements = [.. contract.Requirements, requirement] }, $"requirement {requirement.Code} added");
        }, ct);
    }

    public Task<CommandResult> AddMilestoneAsync(AddMilestone c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var pid = new ProgramId(c.ProgramId);
        var spec = new CommandSpec(SourceContext, nameof(AddMilestone), Permissions.ProgramsManage, new ResourceRef("Milestone", null, pid));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var program = await ActiveProgramAsync(uow, c.ProgramId, context, ct);
            var milestone = new MilestoneInfo(0, program.ProgramId, BusinessCode.RequireName(c.Title, "Milestone"), c.DueDate, null, null);
            var id = await uow.Store<IProgramsStore>().InsertMilestoneAsync(milestone, now, context.Actor, ct);
            return Outcome(context, program.ProgramId, "Milestone", Key(id), "Created", $"Milestone \"{milestone.Title}\" due {c.DueDate:yyyy-MM-dd} added to {program.Code}.", null, milestone with { MilestoneId = id });
        }, ct);
    }

    public Task<CommandResult> CompleteMilestoneAsync(CompleteMilestone c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var id = Key(c.MilestoneId);
        var spec = new CommandSpec(SourceContext, nameof(CompleteMilestone), Permissions.ProgramsManage, ResourceRef.Of("Milestone", id));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<IProgramsStore>();
            var milestone = await store.FindMilestoneAsync(c.MilestoneId, ct);
            if (milestone is null || !Visible(context, milestone.ProgramId))
            {
                throw new CommandRejectedException("milestone_not_found", $"Milestone {id} does not exist.");
            }

            if (milestone.CompletedOn is not null)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, "Already complete."));
            }

            if (c.CompletedOn > DateOnly.FromDateTime(now.UtcDateTime.AddDays(1)))
            {
                throw new CommandRejectedException("future_date", "A milestone cannot be completed in the future.");
            }

            var next = milestone with { CompletedOn = c.CompletedOn, CompletedBy = context.Actor };
            await store.UpdateMilestoneAsync(next, ct);
            return Outcome(context, milestone.ProgramId, "Milestone", id, "Completed", $"Milestone \"{milestone.Title}\" completed {c.CompletedOn:yyyy-MM-dd}.", milestone, next);
        }, ct);
    }

    /// <summary>
    /// Records a funding action (a contract modification). Total funds obligated never exceed the
    /// ceiling, and a de-obligation never takes the total below zero.
    /// </summary>
    public Task<CommandResult> AddFundingAsync(AddFunding c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ContractAsync(nameof(AddFunding), c.ContractId, c, context, true, async (uow, contract, now) =>
        {
            var mod = BusinessCode.RequireName(c.Modification, "Contract action");
            string? clin = null;
            if (!string.IsNullOrWhiteSpace(c.Clin))
            {
                clin = ClinCode(c.Clin);
                if (contract.Clins.All(x => x.Clin != clin))
                {
                    throw new CommandRejectedException("unknown_clin", $"Contract {contract.Number} has no CLIN {clin}.");
                }
            }

            var total = contract.Funded + c.Amount;
            if (c.Amount == 0 || Math.Abs(c.Amount) > 10_000_000_000m)
            {
                throw new CommandRejectedException("invalid_amount", "Give a non-zero amount; a negative amount de-obligates.");
            }

            if (total < 0)
            {
                throw new CommandRejectedException("negative_funding", $"Contract {contract.Number} has {contract.Funded:0.00} obligated; it cannot go below zero.");
            }

            if (contract.Ceiling is { } ceiling && total > ceiling)
            {
                throw new CommandRejectedException("over_ceiling", $"That would obligate {total:0.00} against a ceiling of {ceiling:0.00}. Modify the ceiling first.");
            }

            var funding = new FundingInfo(0, contract.ContractId, clin, mod, c.Amount, c.ObligatedOn, context.Actor, now);
            var id = await uow.Store<IProgramsStore>().InsertFundingAsync(funding, ct);
            return (contract with { Funding = [.. contract.Funding, funding with { FundingId = id }] },
                $"{(c.Amount > 0 ? "obligated" : "de-obligated")} {Math.Abs(c.Amount):0.00} ({mod}); {total:0.00} funded");
        }, ct);
    }

    public Task<CommandResult> AddWbsElementAsync(AddWbsElement c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var pid = new ProgramId(c.ProgramId);
        var spec = new CommandSpec(SourceContext, nameof(AddWbsElement), Permissions.ProgramsManage, new ResourceRef("Wbs", c.Code, pid));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var program = await ActiveProgramAsync(uow, c.ProgramId, context, ct);
            var code = WbsCode(c.Code);
            var parent = string.IsNullOrWhiteSpace(c.ParentCode) ? null : WbsCode(c.ParentCode);
            var existing = await uow.Store<IProgramsLookup>().ListWbsAsync(program.ProgramId, ct);
            if (parent is null ? code.Contains('.', StringComparison.Ordinal) : !code.StartsWith(parent + ".", StringComparison.Ordinal) || code[(parent.Length + 1)..].Contains('.', StringComparison.Ordinal))
            {
                throw new CommandRejectedException("invalid_wbs", parent is null ? $"A top-level element has a single number (1, 2…), not {code}." : $"An element under {parent} is numbered {parent}.n, not {code}.");
            }

            if (parent is not null && existing.All(e => e.Code != parent))
            {
                throw new CommandRejectedException("unknown_wbs", $"{program.Code} has no WBS element {parent}.");
            }

            var element = new WbsElement(program.ProgramId, code, parent, BusinessCode.RequireName(c.Title, "WBS element"));
            if (!await uow.Store<IProgramsStore>().InsertWbsAsync(element, now, context.Actor, ct))
            {
                throw new CommandRejectedException("wbs_exists", $"{program.Code} already has WBS {code}.");
            }

            return Outcome(context, program.ProgramId, "Wbs", code, "Created", $"WBS {code} {element.Title} added to {program.Code}.", null, element);
        }, ct);
    }

    // ------------------------------------------------------------------ helpers

    private static string WbsCode(string? raw)
    {
        var code = raw?.Trim() ?? string.Empty;
        var parts = code.Split('.');
        return code.Length is > 0 and <= 40 && parts.All(p => p.Length is > 0 and <= 4 && p.All(char.IsAsciiDigit) && p[0] != '0')
            ? code
            : throw new CommandRejectedException("invalid_wbs", $"'{raw}' is not a WBS code. Use dotted numbers such as 1, 1.2 or 1.2.3.");
    }

    private Task<CommandResult> ProgramAsync<TCommand>(string commandType, Guid programId, TCommand command, CommandContext context, bool reasonRequired, Func<ProgramInfo, (ProgramInfo Next, string Message)> change, CancellationToken ct)
    {
        var pid = new ProgramId(programId);
        var id = programId.ToString("D", CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, commandType, Permissions.ProgramsManage, new ResourceRef(ProgramEntity, id, pid), reasonRequired);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, _) =>
        {
            var program = await uow.Store<IProgramsLookup>().FindProgramAsync(programId, ct)
                ?? throw new CommandRejectedException("program_not_found", $"Program {id} does not exist.");
            var (next, message) = change(program);
            if (next == program)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, "Nothing changed."));
            }

            await uow.Store<IProgramsStore>().UpdateProgramAsync(next, ct);
            return Outcome(context, programId, ProgramEntity, id, message, $"Program {program.Code} {message}.", program, next);
        }, ct);
    }

    private Task<CommandResult> ContractAsync<TCommand>(
        string commandType,
        long contractId,
        TCommand command,
        CommandContext context,
        bool reasonRequired,
        Func<IKernelUnitOfWork, ContractInfo, DateTimeOffset, Task<(ContractInfo Next, string Message)>> change,
        CancellationToken ct,
        bool requireActive = true)
    {
        var id = Key(contractId);
        var spec = new CommandSpec(SourceContext, commandType, Permissions.ProgramsManage, ResourceRef.Of(ContractEntity, id), reasonRequired);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var contract = await VisibleContractAsync(uow, contractId, context, ct);
            if (requireActive && contract.Status != ContractStatus.Active)
            {
                throw new CommandRejectedException("contract_not_active", $"Contract {contract.Number} is {contract.Status}.");
            }

            var (next, message) = await change(uow, contract, now);
            return Outcome(context, contract.ProgramId, ContractEntity, id, commandType, $"Contract {contract.Number}: {message}.", contract, next);
        }, ct);
    }

    private Task<CommandResult> DeliveryAsync<TCommand>(string commandType, long deliveryId, TCommand command, CommandContext context, Func<ContractInfo, DeliveryInfo, (DeliveryInfo Next, string Message)> change, CancellationToken ct)
    {
        var id = Key(deliveryId);
        var spec = new CommandSpec(SourceContext, commandType, Permissions.ProgramsManage, ResourceRef.Of("Delivery", id), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, _) =>
        {
            var reference = await uow.Store<IProgramsLookup>().FindDeliveryAsync(deliveryId, ct);
            if (reference is null || !Visible(context, reference.ProgramId))
            {
                throw new CommandRejectedException("delivery_not_found", $"Delivery {id} does not exist.");
            }

            if (reference.Delivery.Cancelled)
            {
                throw new CommandRejectedException("delivery_cancelled", $"Delivery {id} is cancelled.");
            }

            var contract = await VisibleContractAsync(uow, reference.Delivery.ContractId, context, ct);
            if (contract.Status != ContractStatus.Active)
            {
                throw new CommandRejectedException("contract_not_active", $"Contract {contract.Number} is {contract.Status}.");
            }

            var (next, message) = change(contract, reference.Delivery);
            await uow.Store<IProgramsStore>().UpdateDeliveryAsync(next, ct);
            return Outcome(context, contract.ProgramId, "Delivery", id, commandType, $"Delivery {id} on {contract.Number} CLIN {next.Clin} {message}.", reference.Delivery, next);
        }, ct);
    }

    private static async Task<ProgramInfo> ActiveProgramAsync(IKernelUnitOfWork uow, Guid programId, CommandContext context, CancellationToken ct)
    {
        var program = await uow.Store<IProgramsLookup>().FindProgramAsync(programId, ct);
        if (program is null || !Visible(context, programId))
        {
            throw new CommandRejectedException("program_not_found", $"Program {programId} does not exist.");
        }

        return program.Status == ProgramStatus.Active ? program : throw new CommandRejectedException("program_closed", $"Program {program.Code} is closed.");
    }

    private static async Task<ContractInfo> VisibleContractAsync(IKernelUnitOfWork uow, long contractId, CommandContext context, CancellationToken ct)
    {
        var contract = (await uow.Store<IProgramsLookup>().ListContractsAsync(null, ct)).FirstOrDefault(x => x.ContractId == contractId);
        return contract is not null && Visible(context, contract.ProgramId)
            ? contract
            : throw new CommandRejectedException("contract_not_found", $"Contract {contractId} does not exist.");
    }

    private static void CheckDelivery(ContractInfo contract, ClinInfo line, long? deliveryId, decimal quantity, DateOnly due)
    {
        if (line.PartNumber is null)
        {
            throw new CommandRejectedException("clin_has_no_part", $"CLIN {line.Clin} names no part, so nothing can be delivered against it. Data, services and other non-part CLINs are tracked as milestones.");
        }

        if (quantity <= 0 || quantity > 100_000_000)
        {
            throw new CommandRejectedException("invalid_quantity", "Quantity must be above zero.");
        }

        if (due < contract.PeriodStart || due > contract.PeriodEnd)
        {
            throw new CommandRejectedException("outside_period", $"Due date must fall in the period of performance, {contract.PeriodStart:yyyy-MM-dd} to {contract.PeriodEnd:yyyy-MM-dd}.");
        }

        var scheduled = contract.Deliveries.Where(d => d.Clin == line.Clin && !d.Cancelled && d.DeliveryId != deliveryId).Sum(d => d.Quantity);
        if (line.Quantity is { } limit && scheduled + quantity > limit)
        {
            throw new CommandRejectedException("over_clin_quantity", $"CLIN {line.Clin} is for {limit:0.###}; {scheduled:0.###} is already scheduled, so at most {limit - scheduled:0.###} more.");
        }
    }

    private static void ValidatePeriod(DateOnly start, DateOnly end, decimal? ceiling)
    {
        if (end < start)
        {
            throw new CommandRejectedException("invalid_period", "The period of performance must end on or after it starts.");
        }

        if (ceiling is < 0)
        {
            throw new CommandRejectedException("invalid_ceiling", "The ceiling cannot be negative.");
        }
    }

    private static string ClinCode(string? raw)
    {
        var code = raw?.Trim().ToUpperInvariant() ?? string.Empty;
        return code.Length is >= 4 and <= 6 && code.All(char.IsAsciiLetterOrDigit)
            ? code
            : throw new CommandRejectedException("invalid_clin", $"'{raw}' is not a CLIN. Use four digits (0001), with two letters for a SLIN (0001AA).");
    }

    private static bool Visible(CommandContext context, Guid program) => context.Principal.Programs.Contains(new ProgramId(program));

    private CommandOutcome Outcome(CommandContext context, Guid programId, string kind, string id, string change, string message, object? before, object? after)
    {
        var pid = new ProgramId(programId);
        return new CommandOutcome(
            CommandResult.Ok(id, message),
            [pipeline.Event(new ProgramChanged(programId, kind, id, change), context, SourceContext, kind, id, pid)],
            new AuditChange(kind, id, before is null ? null : CanonicalJson.Serialize(before), after is null ? null : CanonicalJson.Serialize(after), pid));
    }

    private static string Key(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? Trim(string? value, int max)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > max ? text[..max] : text;
    }
}
