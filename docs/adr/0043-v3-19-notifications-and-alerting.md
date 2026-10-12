# ADR 0043: V3.19 notifications and alerting

Status: Accepted

Date: 2026-10-11

## Context

Forge already writes domain events transactionally to the outbox and forwards them through an at-least-once subscription pump. The V3 plan calls for configurable alerts with scopes, role recipients, severity and confidence, channels, templates, delay/reminder/escalation, quiet hours, deduplication, acknowledgment, and notices when conditions clear. Notifications must never reverse the business transaction that raised them, and a deep link must not grant access.

## Decision

Use a notification subscriber over the existing event log. A rule names a trigger event, optional clear event, scope, optional top-level payload equality condition, severity, confidence, recipient/escalation roles, channels, templates, local deep link, timing and deduplication policy. Severity and confidence are independent fields. A condition key is derived from source context and entity identity; one open alert per rule/condition is enforced in PostgreSQL, and recently resolved conditions remain subject to the configured deduplication window.

The subscriber writes an alert projection, per-person inbox rows and pending email deliveries in its own transaction. Recipient selection requires an active Forge identity, a current configured role, and current program access for program-scoped events. Inbox reads and acknowledgments recheck the person's roles, program access and markings. A clear event resolves the open alert, resets the separate resolution acknowledgment state and queues email resolution notices. The application link is restricted to local routes; opening it runs the target API's normal authorization.

Email is sent after alert creation by a worker using a leased delivery row, provider idempotency key and bounded retry backoff. Provider errors remain in delivery state. The adapter sends a Forge person ID to the configured HTTPS provider, which resolves addresses; Forge stores no email address or provider credential in notification rules. Quiet hours use the shared IANA timezone database.

## Consequences

The additive Kernel migration stores rules, alerts, inbox state and delivery retry state. Notifications depend on the existing outbox/subscriber infrastructure rather than a second event pipeline. At-least-once event replay is safe, and an unavailable email service does not affect source transactions. If no email provider is configured, email deliveries remain durably failed and retry according to backoff until configuration is supplied.

The API is exposed at `/api/notifications`. Security administrators and supervisors manage rules; recipients read and acknowledge only their current, scoped inbox entries. The UI bell opens Notifications and counts unread alert and resolution notices.
