# ADR 0013 — V2.2: controlled documents and attachments

## Context
V2 §2–3 asks for controlled documents with internal, external and reference authority, revisions,
files, links to records and data markings. At V2 approval the owner added one requirement: documents
must be attachable to orders (and the other records people work from). V2.3 (applicability and
readiness) and V2.4 (engineering change) build on this increment.

## Decisions
1. **Documents live in Engineering.** Tables `document`, `document_revision`, `document_file` and
   `document_link` in the `engineering` schema; `DocumentService` runs every change through the command
   pipeline (authorization, reason, idempotency, audit with old and new values, events).
2. **Three authorities, held in the database.**
   - *Internal*: Draft → In review → Approved → Released → Superseded, or Obsolete. The author submits;
     a different person with `engineering.documents.approve` approves; a person other than the author
     with `engineering.documents.release` releases, with a reason and an effective date (today or later).
   - *External* (customer, standards body, manufacturer): Draft → Applicable → Superseded, or Obsolete.
     Forge never releases or reviews them; a person other than the one who entered the revision records
     it as the applicable revision, with a reason.
   - *Reference*: Uncontrolled → Obsolete. A kept file, never production authority. A trigger refuses a
     reference document linked as something to follow.
   A trigger refuses statuses that do not belong to the document's authority.
3. **One revision in force, released revisions frozen.** A unique index allows one Released/Applicable
   revision per document; releasing a new one supersedes the previous one with `effective_to` = the new
   effective date, which cannot be earlier than the previous one's. Released, applicable, superseded,
   obsolete and reference revisions cannot have their file, author, approver, releaser or effective date
   changed, superseded and obsolete revisions never change status again, and revisions are never deleted
   (triggers, insufficient_privilege). Author ≠ approver and author ≠ releaser are CHECK constraints.
   Only one draft/in-review/approved revision is open at a time.
4. **Files are stored once, in the database, by SHA-256.** Uploads are raw bodies to
   `/api/engineering/document-files` (at most 50 MB), stored in `document_file` with size and hash
   checked by the database and never updated or deleted. Downloads are served as
   `application/octet-stream`, `Content-Disposition: attachment`, `nosniff` and a sandbox CSP: never
   rendered or executed by Forge.
5. **Attachments are links, and links are history.** A link names one record (item, item revision,
   supplier, program, production order, purchase order, maintenance work order, NCR, work center), a
   purpose (Follow or Information) and optionally a pinned revision; unpinned links show the revision in
   force. A pinned revision must be released or applicable to be followed: drafts never apply.
   Attaching never changes which item revision an order builds. Detaching needs a reason and only marks
   the link detached; the database refuses deletes and any other change. Attaching a plain file creates a
   reference document `REF-nnnnnn` for it.
6. **Engineering does not depend on the contexts that own the records.** `IDocumentTargetResolver`
   (in Engineering's contracts) is implemented by the host, which reads each owner's contract: whether
   the record exists, its label, its program (an NCR takes its order's program) and the permission needed
   to see it. To attach, list or detach, a person needs `engineering.documents.attach` (or
   `engineering.read` to list), the record's read permission and its program; otherwise the record
   "does not exist".
7. **Segregation is the same everywhere.** `DocumentAccess.CanSee` (program and markings) filters
   screens, the document register report, attachment lists and downloads. A program document can only be
   attached to that program's records. Creating or marking a document requires holding the program and
   every marking set (the authorizer checks them as resource attributes). Events carry the document's
   program and markings.
8. **Roles.** New role Document Controller (manage, approve, release, attach). Manufacturing and quality
   engineers manage documents; engineering approvers approve and release; planners, buyers, purchasing
   managers, inspectors, supervisors, maintenance planners and technicians and program managers attach.
   Maintenance planners gain `engineering.read` so they can read the procedures they attach.
9. **API shape.** Attachments use one route family for every record type,
   `/api/engineering/attachments/{type}/{id}` (`/file` for a plain file) and
   `/api/engineering/attachment-links/{id}/detach`, instead of a copy under each owner's routes.
   Item revisions are written `PART:REV`.
10. **Not in this increment.** Facility and capital projects (V2.10) gain attachments when the
    projects context exists. Applicability of documents to orders, mismatches and readiness enforcement
    are V2.3; engineering change records and review policies are V2.4. Effective dates use the UTC date.

## Consequences
- Orders, purchase orders, work orders, NCRs, suppliers and items show their documents and keep the
  history of what was attached and detached, by whom and why.
- The document register report and Forge Analyst ("show the document register") list what needs a
  revision, an approval, a release or a review.
