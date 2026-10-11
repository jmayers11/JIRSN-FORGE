# ADR 0036 — V3.12 Business-record evidence

Status: applied; live quick and selected API verification PASS; full verification running.

Receipts, receiving inspections, production inspections, shipments and FAI now use the existing V2.2 reference attachments and immutable SHA-256 file storage. Packing slips, CoC, CoA, measurements, photographs and FAI reports are evidence types. New evidence attachments remain Reference/Uncontrolled and cannot become production authority or replace structured results.

Signed source evidence has append-only verification records tied to the exact document revision and file hash. The inspector records the external signer, source signature timestamp, meaning, reason and authenticated verifier identity. This attests that the human reviewed the source signature; it does not authenticate the external signer or invent cryptographic verification. V3.16 separately governs Forge authentication assurance and consequential electronic signoff.

New FAI commands require both structured results and a verified signed reference file. Existing FAI records and previously controlled reports remain historical evidence; no past record is rewritten or deleted. Future reports use reference evidence rather than engineering release. A source-file verification does not accept material or complete a receiving inspection.

## Impact review

| Aspect | Decision |
| --- | --- |
| Existing capability | V2.2 files, revisions, attachments, marking/program access, command audit and events |
| Reusable architecture | Extend the same attachment model and host target resolver |
| Partial implementation | V3.11 FAI report used engineering release; new recordings use verified reference evidence |
| Missing capability | Business targets, evidence types and source signature verification supplied here |
| Authoritative owner | Domain owns inspection/receipt/shipment facts; Engineering owns shared document/file evidence storage |
| Entity changes | Target/type enum additions; EvidenceVerification linked to exact existing file revision |
| Database | Engineering 0012 extends enum constraints and adds append-only verification table |
| Domain service | EvidenceService validates visible Reference file and source-signature particulars; Quality validates results independently |
| API | Existing attachment routes support new targets; signature verification write/read routes |
| Events | SignedEvidenceVerified uses the same transactional outbox |
| UI | Existing attachment panels on receiving and shipping; source verification form |
| Authorization | QualityInspect for signature verification; target read permission plus document program/markings |
| Audit | Command reason, verifier identity, exact file and source signature particulars |
| Integration | Existing storage and contracts; no new external dependency |
| Failure behavior | Missing target/document returns hidden 404; missing signature/results rejected; transaction fails atomically |
| Performance | Target lookup follows contracts; large-list target lookups require indexing/paging review at V3.28 |
| Migration | Additive constraints/table; old FAI history retained; migration-idempotency count advanced |
| Testing | Certificate does not complete inspection; results do not replace required signed evidence; denied/hidden targets and V3.11 regression |
| Dependencies | V3.11 receiving/FAI and V2.2 attachments |
| Product decisions | Source-signature verification is explicit human review; stronger Forge assurance remains V3.16 |

## Validation

Not yet executed. Do not declare this increment complete until staged implementation is applied, build/API/full regression gates pass and this section records their actual results.
