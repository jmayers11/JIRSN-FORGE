using Forge.Competency.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Competency;

/// <summary>
/// Qualification types, prerequisites, training and certification. A person becomes qualified only
/// through <see cref="CertifyAsync"/> by someone holding certification authority who is not that
/// person. Training records never grant a qualification on their own.
/// </summary>
public sealed class CompetencyService(CommandPipeline pipeline)
{
    public const string SourceContext = "Competency";
    private const string TypeEntity = "QualificationType";
    private const string GrantEntity = "PersonQualification";

    public Task<CommandResult> DefineTypeAsync(DefineQualificationType command, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var code = BusinessCode.Require(command.Code, "Qualification");
        var spec = new CommandSpec(SourceContext, nameof(DefineQualificationType), Permissions.CompetencyManage, ResourceRef.Of(TypeEntity, code));
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var name = BusinessCode.RequireName(command.Name, "Qualification");
            if (command.ValidityDays is <= 0 or > 3650)
            {
                throw new CommandRejectedException("invalid_validity", "Validity must be between 1 and 3650 days, or empty for no expiry.");
            }

            var store = uow.Store<ICompetencyStore>();
            if (await store.FindTypeAsync(code, ct) is not null)
            {
                throw new CommandRejectedException("code_exists", $"Qualification type {code} already exists.");
            }

            var type = new QualificationTypeInfo(code, name, command.Kind, command.ValidityDays, []);
            await store.InsertTypeAsync(type, now, context.Actor, ct);
            return new CommandOutcome(
                CommandResult.Ok(code, $"Qualification type {code} defined."),
                [pipeline.Event(new QualificationTypeDefined(code, name, command.Kind.ToString(), command.ValidityDays), context, SourceContext, TypeEntity, code)],
                new AuditChange(TypeEntity, code, null, CanonicalJson.Serialize(type)));
        }, ct);
    }

    public Task<CommandResult> AddPrerequisiteAsync(AddPrerequisite command, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var code = BusinessCode.Require(command.Code, "Qualification");
        var spec = new CommandSpec(SourceContext, nameof(AddPrerequisite), Permissions.CompetencyManage, ResourceRef.Of(TypeEntity, code));
        return pipeline.ExecuteAsync(spec, command, context, async (uow, _) =>
        {
            var prerequisite = BusinessCode.Require(command.PrerequisiteCode, "Prerequisite");
            var store = uow.Store<ICompetencyStore>();
            var type = await store.FindTypeAsync(code, ct);
            if (type is null || await store.FindTypeAsync(prerequisite, ct) is null)
            {
                throw new CommandRejectedException("unknown_qualification", "Both qualification types must exist.");
            }

            if (type.PrerequisiteAlternatives?.Any(group => group.QualificationCodes.Contains(prerequisite, StringComparer.Ordinal)) == true)
            {
                throw new CommandRejectedException("prerequisite_conflict", $"{prerequisite} is already an alternative prerequisite for {code}; it cannot also be mandatory.");
            }

            var edges = await store.ListPrerequisitesAsync(ct);
            if (code == prerequisite || Reaches(edges, prerequisite, code))
            {
                throw new CommandRejectedException("prerequisite_cycle", $"{prerequisite} already depends on {code}; adding this would create a cycle.");
            }

            if (!await store.AddPrerequisiteAsync(code, prerequisite, ct))
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(code, "Prerequisite already defined."));
            }

            return new CommandOutcome(
                CommandResult.Ok(code, $"{prerequisite} is now required before {code}."),
                [pipeline.Event(new QualificationPrerequisiteAdded(code, prerequisite), context, SourceContext, TypeEntity, code)],
                new AuditChange(TypeEntity, code, null, CanonicalJson.Serialize(new { prerequisite })));
        }, ct);
    }

    public Task<CommandResult> DefinePrerequisiteAlternativesAsync(DefinePrerequisiteAlternatives command, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var code = BusinessCode.Require(command.Code, "Qualification");
        var groupCode = BusinessCode.Require(command.GroupCode, "Prerequisite group");
        var spec = new CommandSpec(SourceContext, nameof(DefinePrerequisiteAlternatives), Permissions.CompetencyManage, ResourceRef.Of(TypeEntity, code));
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var name = BusinessCode.RequireName(command.Name, "Prerequisite group");
            if (command.PrerequisiteCodes is null)
            {
                throw new CommandRejectedException("alternatives_required", "Provide at least two qualification alternatives.");
            }

            var prerequisites = command.PrerequisiteCodes.Select(value => BusinessCode.Require(value, "Prerequisite"))
                .Order(StringComparer.Ordinal).ToArray();
            if (prerequisites.Length < 2 || prerequisites.Distinct(StringComparer.Ordinal).Count() != prerequisites.Length)
            {
                throw new CommandRejectedException("alternatives_invalid", "An alternative prerequisite group requires at least two distinct qualifications.");
            }

            var store = uow.Store<ICompetencyStore>();
            var type = await RequireTypeAsync(store, code, ct);
            foreach (var prerequisite in prerequisites)
            {
                if (await store.FindTypeAsync(prerequisite, ct) is null)
                {
                    throw new CommandRejectedException("unknown_qualification", $"Prerequisite qualification {prerequisite} does not exist.");
                }
            }

            if (prerequisites.Any(prerequisite => type.Prerequisites.Contains(prerequisite, StringComparer.Ordinal)))
            {
                throw new CommandRejectedException("prerequisite_conflict", "An alternative prerequisite cannot also be mandatory for the same qualification.");
            }

            var existing = type.PrerequisiteAlternatives?.SingleOrDefault(group => group.GroupCode == groupCode);
            if (existing is not null)
            {
                if (existing.Name == name && existing.QualificationCodes.Order(StringComparer.Ordinal).SequenceEqual(prerequisites, StringComparer.Ordinal))
                {
                    return CommandOutcome.NoChange(CommandResult.NoChange(code, $"Alternative prerequisite group {groupCode} already matches."));
                }

                throw new CommandRejectedException("prerequisite_group_conflict", $"Alternative prerequisite group {groupCode} already exists with a different definition.");
            }

            var edges = await store.ListPrerequisitesAsync(ct);
            var cycleMember = prerequisites.FirstOrDefault(prerequisite => code == prerequisite || Reaches(edges, prerequisite, code));
            if (cycleMember is not null)
            {
                throw new CommandRejectedException("prerequisite_cycle", $"{cycleMember} already depends on {code}; adding this alternative group would create a cycle.");
            }

            await store.DefinePrerequisiteAlternativesAsync(code, groupCode, name, prerequisites, context.Actor, now, ct);
            return new CommandOutcome(
                CommandResult.Ok(code, $"Alternative prerequisite group {groupCode} defined for {code}."),
                [pipeline.Event(new QualificationPrerequisiteAlternativesDefined(code, groupCode, name, prerequisites), context, SourceContext, TypeEntity, code)],
                new AuditChange(TypeEntity, code, null, CanonicalJson.Serialize(new { groupCode, name, prerequisites })));
        }, ct);
    }

    public Task<CommandResult> StartTrainingAsync(StartTraining command, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var code = BusinessCode.Require(command.Code, "Qualification");
        var spec = new CommandSpec(SourceContext, nameof(StartTraining), Permissions.CompetencyManage, ResourceRef.Of(GrantEntity, $"{command.PersonId}:{code}"));
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var store = uow.Store<ICompetencyStore>();
            await RequireTypeAsync(store, code, ct);
            await RequireActivePersonAsync(uow, command.PersonId, ct);

            var existing = await store.FindAsync(command.PersonId, code, ct);
            if (existing is { Status: QualificationStatus.InTraining })
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(Id(command.PersonId, code), "Already in training."));
            }

            if (existing is { Status: QualificationStatus.Qualified } && existing.IsValidAt(now))
            {
                throw new CommandRejectedException("already_qualified", "The person already holds this qualification.");
            }

            if (existing is { Status: QualificationStatus.Revoked })
            {
                throw new CommandRejectedException("revoked", "This qualification was revoked. A certifier must review before training restarts.");
            }

            var record = new PersonQualification(command.PersonId.Value, code, QualificationStatus.InTraining, null, null);
            await store.UpsertAsync(record, context.Actor, null, now, ct);
            var id = Id(command.PersonId, code);
            return new CommandOutcome(
                CommandResult.Ok(id, $"Training for {code} started."),
                [pipeline.Event(new TrainingStarted(command.PersonId.Value, code), context, SourceContext, GrantEntity, id)],
                new AuditChange(GrantEntity, id, existing is null ? null : CanonicalJson.Serialize(existing), CanonicalJson.Serialize(record)));
        }, ct);
    }

    public Task<CommandResult> CertifyAsync(CertifyQualification command, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var code = BusinessCode.Require(command.Code, "Qualification");
        var spec = new CommandSpec(SourceContext, nameof(CertifyQualification), Permissions.CompetencyCertify, ResourceRef.Of(GrantEntity, $"{command.PersonId}:{code}"), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            if (context.Principal.PersonId == command.PersonId)
            {
                throw new CommandRejectedException("separation_of_duties", "You cannot certify your own qualification.");
            }

            if (string.IsNullOrWhiteSpace(command.Evidence))
            {
                throw new CommandRejectedException("evidence_required", "Certification requires evidence (assessment record, test result or document reference).");
            }

            var store = uow.Store<ICompetencyStore>();
            var type = await RequireTypeAsync(store, code, ct);
            await RequireActivePersonAsync(uow, command.PersonId, ct);

            var existing = await store.FindAsync(command.PersonId, code, ct);
            if (existing is { Status: QualificationStatus.Revoked })
            {
                throw new CommandRejectedException("revoked", "This qualification was revoked; start training again before certifying.");
            }

            var missing = new List<string>();
            foreach (var prerequisite in type.Prerequisites)
            {
                var held = await store.FindAsync(command.PersonId, prerequisite, ct);
                if (held is null || !held.IsValidAt(now))
                {
                    missing.Add(prerequisite);
                }
            }

            foreach (var group in type.PrerequisiteAlternatives ?? [])
            {
                var satisfied = false;
                foreach (var alternative in group.QualificationCodes)
                {
                    var held = await store.FindAsync(command.PersonId, alternative, ct);
                    if (held is not null && held.IsValidAt(now))
                    {
                        satisfied = true;
                        break;
                    }
                }

                if (!satisfied)
                {
                    missing.Add($"one of [{string.Join(" | ", group.QualificationCodes.Order(StringComparer.Ordinal))}] ({group.Name})");
                }
            }

            if (missing.Count > 0)
            {
                throw new CommandRejectedException("prerequisites_missing", $"Missing valid prerequisites: {string.Join(", ", missing)}.");
            }

            var expires = type.ValidityDays is { } days ? now.AddDays(days) : (DateTimeOffset?)null;
            var record = new PersonQualification(command.PersonId.Value, code, QualificationStatus.Qualified, now, expires);
            await store.UpsertAsync(record, context.Actor, command.Evidence.Trim(), now, ct);
            var id = Id(command.PersonId, code);
            return new CommandOutcome(
                CommandResult.Ok(id, $"{code} certified" + (expires is null ? "." : $" until {expires:yyyy-MM-dd}.")),
                [pipeline.Event(new QualificationGranted(command.PersonId.Value, code, expires, context.Actor), context, SourceContext, GrantEntity, id)],
                new AuditChange(GrantEntity, id, existing is null ? null : CanonicalJson.Serialize(existing), CanonicalJson.Serialize(new { record, evidence = command.Evidence.Trim() })));
        }, ct);
    }

    public Task<CommandResult> RecordTrainingCourseCompletionAsync(RecordTrainingCourseCompletion command, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var course = BusinessCode.Require(command.CourseCode, "Course");
        var spec = new CommandSpec(SourceContext, nameof(RecordTrainingCourseCompletion), Permissions.CompetencyCertify,
            ResourceRef.Of("TrainingCourse", $"{command.PersonId}:{course}"), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var trainer = context.Principal.PersonId;
            if (trainer.Value == Guid.Empty)
                throw new CommandRejectedException("trainer_identity_required", "A verified Forge person identity is required to record course completion.");
            if (trainer == command.PersonId)
                throw new CommandRejectedException("separation_of_duties", "A trainer cannot record their own course completion.");
            var competency = uow.Store<ICompetencyStore>();
            var type = await RequireTypeAsync(competency, course, ct);
            if (type.Kind is not (QualificationKind.General or QualificationKind.Process or QualificationKind.Machine or QualificationKind.SpecialProcess or QualificationKind.Safety))
                throw new CommandRejectedException("invalid_course_code", "Course completion must reference a learner qualification type.");
            await RequireActivePersonAsync(uow, command.PersonId, ct);
            await RequireActivePersonAsync(uow, trainer, ct);
            var competencyLookup = uow.Store<ICompetencyLookup>();
            var trainerQualifications = await competencyLookup.GetQualificationsAsync([trainer], ct);
            var trainerTypes = await competencyLookup.ListTypesAsync(ct);
            if (!trainerTypes.Where(candidate => candidate.Kind == QualificationKind.Trainer)
                .Any(candidate => trainerQualifications.Any(record => record.Code == candidate.Code && record.IsValidAt(now))))
                throw new CommandRejectedException("trainer_authority_required", "Only a currently qualified trainer may record course completion.");
            var version = BusinessCode.Require(command.CourseVersion, "Course version");
            var evidence = BusinessCode.RequireName(command.Evidence, "Evidence");
            if (command.ExpiresAt is { } expiresAt && expiresAt <= now)
                throw new CommandRejectedException("invalid_expiry", "Course completion expiry must be in the future.");
            var completion = new TrainingCourseCompletion(ForgeIds.New(pipeline.Time), command.PersonId.Value, course, version,
                trainer.Value, now, command.ExpiresAt, evidence);
            await competency.InsertTrainingCourseCompletionAsync(completion, ct);
            return new CommandOutcome(
                CommandResult.Ok(completion.CompletionId.ToString("D"), $"Course {course} completion recorded; it does not grant a qualification."),
                [pipeline.Event(new TrainingCourseCompletionRecorded(completion.CompletionId, completion.PersonId, course, version, trainer.Value, completion.ExpiresAt), context, SourceContext, "TrainingCourse", completion.CompletionId.ToString("D"))],
                new AuditChange("TrainingCourseCompletion", completion.CompletionId.ToString("D"), null, CanonicalJson.Serialize(completion)));
        }, ct);
    }

    public Task<CommandResult> RecordPracticalAssessmentAsync(RecordPracticalAssessment command, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var code = BusinessCode.Require(command.QualificationCode, "Qualification");
        var machine = BusinessCode.Require(command.MachineCode, "Machine");
        var activity = BusinessCode.Require(command.ActivityCode, "Activity");
        var spec = new CommandSpec(SourceContext, nameof(RecordPracticalAssessment), Permissions.CompetencyCertify,
            ResourceRef.Of("Machine", $"{machine}:{activity}"), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var assessor = context.Principal.PersonId;
            if (assessor.Value == Guid.Empty)
                throw new CommandRejectedException("assessor_identity_required", "A verified Forge person identity is required to assess practical competency.");
            if (assessor == command.PersonId)
                throw new CommandRejectedException("separation_of_duties", "A person cannot assess their own practical competency.");
            var competency = uow.Store<ICompetencyStore>();
            await RequireTypeAsync(competency, code, ct);
            await RequireActivePersonAsync(uow, command.PersonId, ct);
            await RequireActivePersonAsync(uow, assessor, ct);
            var profile = await uow.Store<ICompetencyMachineContext>().FindCompetencyMachineActivityAsync(machine, activity, ct)
                ?? throw new CommandRejectedException("machine_activity_not_found", $"{activity} is not configured for {machine}.");
            if (!profile.OperatorQualifications.Contains(code, StringComparer.Ordinal))
                throw new CommandRejectedException("qualification_not_required", $"{code} is not a qualification required by {activity} on {machine}.");
            var held = await competency.FindAsync(command.PersonId, code, ct);
            if (held is null || !held.IsValidAt(now))
                throw new CommandRejectedException("qualification_required", $"A current {code} qualification is required before practical assessment.");
            var course = (await uow.Store<ICompetencyLookup>().GetTrainingCourseCompletionsAsync([command.PersonId], ct))
                .Where(item => item.CourseCode == code && (item.ExpiresAt is null || item.ExpiresAt > now))
                .OrderByDescending(item => item.CompletedAt).FirstOrDefault();
            if (course is null)
                throw new CommandRejectedException("course_completion_required", $"Record current {code} course completion before the practical assessment.");
            var evidence = BusinessCode.RequireName(command.Evidence, "Evidence");
            var version = BusinessCode.Require(command.AssessmentVersion, "Assessment version");
            var restrictions = string.IsNullOrWhiteSpace(command.Restrictions) ? null : command.Restrictions.Trim();
            if (restrictions?.Length > 500)
                throw new CommandRejectedException("restrictions_too_long", "Restrictions must be 500 characters or fewer.");
            var assessment = new PracticalAssessment(ForgeIds.New(pipeline.Time), command.PersonId.Value, code, machine, activity,
                assessor.Value, command.Passed, version, now, evidence, restrictions);
            await competency.InsertPracticalAssessmentAsync(assessment, ct);
            return new CommandOutcome(
                CommandResult.Ok(assessment.AssessmentId.ToString("D"), command.Passed ? "Practical assessment passed; machine authorization still requires approval." : "Practical assessment recorded as not passed."),
                [pipeline.Event(new PracticalAssessmentRecorded(assessment.AssessmentId, assessment.PersonId, code, machine, activity, assessor.Value, command.Passed, version), context, SourceContext, "PracticalAssessment", assessment.AssessmentId.ToString("D"))],
                new AuditChange("PracticalAssessment", assessment.AssessmentId.ToString("D"), null, CanonicalJson.Serialize(assessment)));
        }, ct);
    }

    public Task<CommandResult> AuthorizeMachineOperationAsync(AuthorizeMachineOperation command, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var code = BusinessCode.Require(command.QualificationCode, "Qualification");
        var machine = BusinessCode.Require(command.MachineCode, "Machine");
        var activity = BusinessCode.Require(command.ActivityCode, "Activity");
        var spec = new CommandSpec(SourceContext, nameof(AuthorizeMachineOperation), Permissions.CompetencyMachineAuthorize,
            ResourceRef.Of("MachineAuthorization", $"{command.PersonId}:{machine}:{activity}"), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var approver = context.Principal.PersonId;
            if (approver.Value == Guid.Empty)
                throw new CommandRejectedException("approver_identity_required", "A verified Forge person identity is required to approve machine authorization.");
            if (approver == command.PersonId)
                throw new CommandRejectedException("separation_of_duties", "A person cannot approve their own machine authorization.");
            await RequireActivePersonAsync(uow, command.PersonId, ct);
            var competency = uow.Store<ICompetencyStore>();
            await RequireTypeAsync(competency, code, ct);
            var profile = await uow.Store<ICompetencyMachineContext>().FindCompetencyMachineActivityAsync(machine, activity, ct)
                ?? throw new CommandRejectedException("machine_activity_not_found", $"{activity} is not configured for {machine}.");
            if (!profile.OperatorQualifications.Contains(code, StringComparer.Ordinal))
                throw new CommandRejectedException("qualification_not_required", $"{code} is not a qualification required by {activity} on {machine}.");
            var qualification = await competency.FindAsync(command.PersonId, code, ct);
            if (qualification is null || !qualification.IsValidAt(now))
                throw new CommandRejectedException("qualification_required", $"A current {code} qualification is required for machine authorization.");
            var assessment = (await uow.Store<ICompetencyLookup>().GetPracticalAssessmentsAsync([command.PersonId], ct))
                .SingleOrDefault(item => item.AssessmentId == command.AssessmentId);
            if (assessment is null || !assessment.Passed || assessment.QualificationCode != code
                || assessment.MachineCode != machine || assessment.ActivityCode != activity)
                throw new CommandRejectedException("passing_assessment_required", "A matching, passing practical assessment is required.");
            var course = (await uow.Store<ICompetencyLookup>().GetTrainingCourseCompletionsAsync([command.PersonId], ct))
                .Where(item => item.CourseCode == code && (item.ExpiresAt is null || item.ExpiresAt > now))
                .OrderByDescending(item => item.CompletedAt).FirstOrDefault();
            if (course is null)
                throw new CommandRejectedException("course_completion_required", $"A current {code} course completion is required.");
            if (command.ExpiresAt is { } expiry && (expiry <= now || (qualification.ExpiresAt is { } qualificationExpiry && expiry > qualificationExpiry)
                || (course.ExpiresAt is { } courseExpiry && expiry > courseExpiry)))
                throw new CommandRejectedException("invalid_expiry", "Machine authorization must be future dated and cannot outlast its qualification or course completion.");
            var previous = await competency.FindMachineAuthorizationAsync(command.PersonId, machine, activity, code, ct);
            var revision = (previous?.Revision ?? 0) + 1;
            var authorization = new MachineAuthorization(command.PersonId.Value, machine, activity, code,
                MachineAuthorizationStatus.Authorized, command.IndependentOperationAllowed, now, command.ExpiresAt, revision,
                course.CompletionId, course.ExpiresAt, assessment.AssessmentId, BusinessCode.RequireName(command.Evidence, "Evidence"),
                string.IsNullOrWhiteSpace(command.Restrictions) ? null : command.Restrictions.Trim(), approver.Value.ToString("D"));
            if (authorization.Restrictions?.Length > 500)
                throw new CommandRejectedException("restrictions_too_long", "Restrictions must be 500 characters or fewer.");
            await competency.UpsertMachineAuthorizationAsync(authorization, context.Reason ?? string.Empty, context.Actor, now, ct);
            var key = $"{command.PersonId}:{machine}:{activity}:{code}";
            return new CommandOutcome(
                CommandResult.Ok(key, $"{machine} {activity} authorization issued with revision {revision}."),
                [pipeline.Event(new MachineAuthorizationChanged(command.PersonId.Value, machine, activity, code, authorization.Status.ToString(), authorization.IndependentOperationAllowed, revision, course.CompletionId, assessment.AssessmentId), context, SourceContext, "MachineAuthorization", key)],
                new AuditChange("MachineAuthorization", key, previous is null ? null : CanonicalJson.Serialize(previous), CanonicalJson.Serialize(authorization)));
        }, ct);
    }

    public Task<CommandResult> ChangeMachineAuthorizationAsync(ChangeMachineAuthorization command, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var code = BusinessCode.Require(command.QualificationCode, "Qualification");
        var machine = BusinessCode.Require(command.MachineCode, "Machine");
        var activity = BusinessCode.Require(command.ActivityCode, "Activity");
        var key = $"{command.PersonId}:{machine}:{activity}:{code}";
        var spec = new CommandSpec(SourceContext, nameof(ChangeMachineAuthorization), Permissions.CompetencyMachineAuthorize,
            ResourceRef.Of("MachineAuthorization", key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            if (!Enum.IsDefined(command.Status))
                throw new CommandRejectedException("invalid_authorization_status", "Choose Authorized, Suspended or Revoked.");
            var store = uow.Store<ICompetencyStore>();
            var previous = await store.FindMachineAuthorizationAsync(command.PersonId, machine, activity, code, ct)
                ?? throw new CommandRejectedException("authorization_not_found", "No machine authorization exists for this person and operation.");
            if (previous.Status == command.Status)
                return CommandOutcome.NoChange(CommandResult.NoChange(key, $"Machine authorization is already {command.Status}."));
            var updated = previous with { Status = command.Status, Revision = previous.Revision + 1 };
            await store.UpsertMachineAuthorizationAsync(updated, context.Reason ?? string.Empty, context.Actor, now, ct);
            return new CommandOutcome(
                CommandResult.Ok(key, $"Machine authorization {command.Status.ToString().ToLowerInvariant()}."),
                [pipeline.Event(new MachineAuthorizationChanged(command.PersonId.Value, machine, activity, code, updated.Status.ToString(), updated.IndependentOperationAllowed, updated.Revision, updated.CourseCompletionId, updated.AssessmentId), context, SourceContext, "MachineAuthorization", key)],
                new AuditChange("MachineAuthorization", key, CanonicalJson.Serialize(previous), CanonicalJson.Serialize(updated)));
        }, ct);
    }

    public Task<CommandResult> SuspendAsync(SuspendQualification command, CommandContext context, CancellationToken ct) =>
        ChangeStatusAsync(nameof(SuspendQualification), command.PersonId, command.Code, QualificationStatus.Suspended, command, context, ct);

    public Task<CommandResult> RevokeAsync(RevokeQualification command, CommandContext context, CancellationToken ct) =>
        ChangeStatusAsync(nameof(RevokeQualification), command.PersonId, command.Code, QualificationStatus.Revoked, command, context, ct);

    private Task<CommandResult> ChangeStatusAsync<TCommand>(string commandType, PersonId person, string rawCode, QualificationStatus status, TCommand command, CommandContext context, CancellationToken ct)
    {
        var code = BusinessCode.Require(rawCode, "Qualification");
        var spec = new CommandSpec(SourceContext, commandType, Permissions.CompetencyCertify, ResourceRef.Of(GrantEntity, $"{person}:{code}"), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var store = uow.Store<ICompetencyStore>();
            var existing = await store.FindAsync(person, code, ct)
                ?? throw new CommandRejectedException("not_found", "The person has no record for this qualification.");
            var id = Id(person, code);
            if (existing.Status == status)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, $"Already {status}."));
            }

            var record = existing with { Status = status };
            await store.UpsertAsync(record, context.Actor, null, now, ct);
            Kernel.Events.EventEnvelope evt = status == QualificationStatus.Revoked
                ? pipeline.Event(new QualificationRevoked(person.Value, code), context, SourceContext, GrantEntity, id)
                : pipeline.Event(new QualificationSuspended(person.Value, code), context, SourceContext, GrantEntity, id);
            return new CommandOutcome(
                CommandResult.Ok(id, $"{code} {status.ToString().ToLowerInvariant()}."),
                [evt],
                new AuditChange(GrantEntity, id, CanonicalJson.Serialize(existing), CanonicalJson.Serialize(record)));
        }, ct);
    }

    private static string Id(PersonId person, string code) => $"{person}:{code}";

    private static async Task<QualificationTypeInfo> RequireTypeAsync(ICompetencyStore store, string code, CancellationToken ct) =>
        await store.FindTypeAsync(code, ct) ?? throw new CommandRejectedException("unknown_qualification", $"Qualification type {code} does not exist.");

    private static async Task RequireActivePersonAsync(IKernelUnitOfWork uow, PersonId person, CancellationToken ct)
    {
        var record = await uow.People.FindAsync(person, ct) ?? throw new CommandRejectedException("person_not_found", "Person not found.");
        if (!record.IsActive)
        {
            throw new CommandRejectedException("person_inactive", "Person is not active.");
        }
    }

    // True if 'from' depends (directly or indirectly) on 'target'.
    private static bool Reaches(IReadOnlyList<(string Code, string Prerequisite)> edges, string from, string target)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>([from]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == target)
            {
                return true;
            }

            if (!seen.Add(current))
            {
                continue;
            }

            foreach (var edge in edges.Where(e => e.Code == current))
            {
                stack.Push(edge.Prerequisite);
            }
        }

        return false;
    }
}
