# MedQueue cloud adapter v1

Expose these endpoints beneath the configured HTTPS base URL. Accept an Authorization header using the Bearer scheme on every endpoint. Use a credential scoped to one immutable practice and the minimum permissions required for the routes below. Return JSON using these field names, UTC ISO 8601 timestamps, and HTTP 200 for success. The adapter is responsible for authenticated EHR access and site/practice authorization on every request.

| Request | Response |
| --- | --- |
| GET /identity | Object with Protocol set to medqueue/1, a human-readable Practice and stable PracticeId. Reserve the value demo for the bundled training provider. |
| GET /appointments?from=…&until=… | Array of objects with Id, PatientId and Start for appointments from the inclusive start up to the exclusive end. Omit canceled visits. |
| GET /visits/{Id} | Object with Id, PatientId, Name, Start, Revision, CheckedIn and Records. CheckedIn is null or a UTC timestamp. Records is an array with stable Id, Kind, Title and Body fields. Return a complete, clinically validated chart for the visit and project concurrent EHR edits into Revision. |
| POST /changes | Accept a change object. On successful application or an exact replay, return EventId and Revision. Return 409 only for a stale BaseRevision with the event unapplied. |

A change carries Id (the stable event ID), VisitId, PatientId, Actor, At, Kind, BaseRevision, and Record (null for check-in). Supported kinds are check-in, record, prescription, and signed-form. For check-in use At as the check-in time. Other changes upsert a chart item by its stable Record.Id. Preserve immutable signed form snapshots that include the version, signed text, signer and timestamp. Validate the actor, visit, patient, record category, and practice before applying every change.

Atomically validate BaseRevision, apply the event, and persist its acknowledgement keyed by practice and event ID. Check for a previously applied event before testing the revision and return the original acknowledgement. This handles a lost response after a successful commit and prevents duplicate clinical events. Return a new Revision after a successful write. If multiple visits share a chart, revisions must capture concurrent chart changes across those visits. Keep the idempotency history for the full possible downtime/retry period. MedQueue retries until it receives an acknowledgement and retains stale changes for explicit review.

Adapter verification checks the identity and schedule read endpoints. Validate actual record reads, writes, revisions, duplicate delivery and signed form mapping against a staging practice before production. Avoid patient data and credentials in URLs, application telemetry, and HTTP logs. Return sanitized error bodies.
