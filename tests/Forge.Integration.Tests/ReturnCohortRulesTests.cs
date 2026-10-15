using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;

namespace Forge.Integration.Tests;

public sealed class ReturnCohortRulesTests
{
 [Fact]
 public void Original_subject_canonical_bytes_and_key_are_preserved_when_new_return_identity_is_absent()
 {
  var subject=new AcceptanceSubjectKey("PART","LOT/1","STORES");
  Assert.Equal("{\"locationCode\":\"STORES\",\"lotNumber\":\"LOT/1\",\"partNumber\":\"PART\"}",CanonicalJson.Serialize(subject));
  Assert.Equal("PART:LOT%2F1:STORES",AcceptanceSubjectReference.Format(subject));
  Assert.True(AcceptanceSubjectReference.TryParse(AcceptanceSubjectReference.Format(subject),out var restored));Assert.Equal(subject,restored);
 }
 [Fact]
 public void Exact_return_cohort_is_distinct_and_parser_does_not_accept_aliases_or_loose_numeric_ids()
 {
  var original=new AcceptanceSubjectKey("PART","SERIAL-1","STORES");var returned=original with{ReturnCohortId=90001};
  Assert.NotEqual(original,returned);Assert.NotEqual(CanonicalJson.Fingerprint(original),CanonicalJson.Fingerprint(returned));
  Assert.Equal("PART:SERIAL-1:STORES:return:90001",AcceptanceSubjectReference.Format(returned));
  Assert.True(AcceptanceSubjectReference.TryParse(AcceptanceSubjectReference.Format(returned),out var restored));Assert.Equal(returned,restored);
  foreach(var suffix in new[]{"return:0","return:-1","return:090001","return:+90001","return:90001 ","return:9223372036854775808","rma:90001"})Assert.False(AcceptanceSubjectReference.TryParse("PART:SERIAL-1:STORES:"+suffix,out _));
 }
}
