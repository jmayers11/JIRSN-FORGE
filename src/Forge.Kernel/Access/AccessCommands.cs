using Forge.Kernel.Identity;
using Forge.Kernel.Security;

namespace Forge.Kernel.Access;

public sealed record RegisterPerson(string IdpSubject, string DisplayName, DataMarking Markings);

public sealed record AssignRole(PersonId PersonId, string Role);

public sealed record RevokeRole(PersonId PersonId, string Role);

public sealed record GrantProgramAccess(PersonId PersonId, ProgramId ProgramId);

public sealed record RevokeProgramAccess(PersonId PersonId, ProgramId ProgramId);
