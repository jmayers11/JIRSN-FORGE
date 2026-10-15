using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;

namespace Forge.Integration.Tests;

public sealed class QmsCapturedOwnerValuesTests
{
    [Fact]
    public void Historic_null_capture_preserves_old_canonical_snapshot_bytes()
    {
        var at=new DateTimeOffset(2026,10,14,15,0,0,TimeSpan.Zero);var reference=new QmsPlanningEvidenceReference("Engineering","DocumentRevision","PROCEDURE","A");
        var snapshot=new QmsPlanningSourceSnapshot(reference,"Engineering","DocumentRevision","PROCEDURE","A","actual-fingerprint",at,at,null,null,null,[],[],false);
        var before=new{snapshot.Reference,snapshot.OwnerContext,snapshot.OwnerType,snapshot.OwnerId,snapshot.OwnerRevision,snapshot.Fingerprint,snapshot.CapturedAt,snapshot.CreatedAt,snapshot.ApprovedAt,snapshot.ClosedAt,snapshot.FileSha256,snapshot.Contributors,snapshot.Sources,snapshot.RequiresLicensedVerification};
        Assert.Equal(CanonicalJson.Serialize(before),CanonicalJson.Serialize(snapshot));Assert.DoesNotContain("capturedCanonicalJson",CanonicalJson.Serialize(snapshot));
    }
    [Fact]
    public void Capture_retains_exact_authorized_owner_values_and_its_fingerprint()
    {
        var owner=new{ownerId="ACTUAL-OWNER",revision="B",humanAdequacy="Partial",completeness="Known gaps",actualQuantity=12.5m};var canonical=CanonicalJson.Serialize(owner);
        var snapshot=new QmsPlanningSourceSnapshot(new("Quality","QmsAudit","actual-UUID"),"Quality","QmsAudit","actual-UUID",null,CanonicalJson.Fingerprint(owner),DateTimeOffset.UtcNow,null,null,null,null,[],[],false,canonical);
        Assert.Equal(snapshot.Fingerprint,CanonicalJson.Sha256Hex(snapshot.CapturedCanonicalJson!));Assert.Contains("capturedCanonicalJson",CanonicalJson.Serialize(snapshot));Assert.Contains("Partial",snapshot.CapturedCanonicalJson);
    }
}
