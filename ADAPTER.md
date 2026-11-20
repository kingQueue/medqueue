# MedQueue cloud adapter v2

MedQueue sends the completed encounter as a structured JSON event. A clinic hosts an HTTPS adapter that maps the event to its charting system's supported write API or import interface. Configure the adapter URL and a practice-scoped credential through **Connect cloud**. Version 2 requires patient verification fields and supports completed encounters. Update version 1 adapters before linking them to this build.

## Endpoints

Accept a Bearer credential on every route. Scope it to one immutable practice and authenticate and authorize each operation against that practice. Use UTC ISO 8601 timestamps for appointment and event times, and return HTTP 200 for a successful request.

| Request | Response |
| --- | --- |
| GET /identity | Object with Protocol set to medqueue/2, a display name in Practice, and a stable PracticeId. Reserve demo for the bundled training provider. |
| GET /appointments?from=…&until=… | Array of Id, PatientId and Start for appointments in the inclusive start and exclusive end. Omit canceled visits. |
| GET /visits/{Id} | Id, PatientId, Name, BirthDate (YYYY-MM-DD), Phone, Start, Revision, CheckedIn, CompletedAt, CompletedBy, and Records. Use null for check-in and completion values before those transitions. Records contain Id, Kind, Title, Body, and EncounterId when documented for a specific encounter. |
| POST /changes | Change object. On successful application or replay, return EventId and Revision. Return 409 only for a stale BaseRevision or a conflicting state transition with the event unapplied. |

The phone format may include presentation punctuation. Supply a contact number in a consistent national or international format with the country code when one is required. MedQueue compares the digits and uses date of birth and full name as separate checks. Use the stable PatientId and VisitId for writes to the destination EHR.

## Event handling

Changes carry Id, VisitId, PatientId, Actor, At, Kind, and BaseRevision, plus Record or Encounter where applicable.

- **check-in** sets the visit check-in time to At. Record and Encounter are null.
- **record, prescription, signed-form, referral** upsert a chart item by its stable Record.Id. Preserve Record.EncounterId and signed form version, text, signer, and timestamp. Store referral entries as requests for the receiving workflow.
- **visit-completed** carries Encounter with ScheduledAt, CheckedInAt, CompletedAt, CompletedBy, and Records. Records contains the chart entries, prescriptions, signed forms, and referral requests documented for this visit, including items already sent as individual events. Use their stable IDs to reconcile those items and finalize the encounter without creating duplicates. Validate that each item belongs to the authenticated practice, patient, and visit.

For example, a completion request can include a fictional referral entry.

```json
{
  "Id": "event-f001",
  "VisitId": "visit-f001",
  "PatientId": "patient-f001",
  "Actor": "training-provider",
  "At": "2026-11-20T15:45:00Z",
  "Kind": "visit-completed",
  "BaseRevision": "rev-17",
  "Record": null,
  "Encounter": {
    "ScheduledAt": "2026-11-20T15:00:00Z",
    "CheckedInAt": "2026-11-20T15:01:00Z",
    "CompletedAt": "2026-11-20T15:45:00Z",
    "CompletedBy": "training-provider",
    "Records": [
      {"Id":"referral-f001","Kind":"Referral","Title":"Training specialty","Body":"Fictional referral request","EncounterId":"visit-f001"}
    ]
  }
}
```

Atomically validate BaseRevision, apply the event, and create its acknowledgement keyed by practice and event ID. Check for a previously applied event before testing the revision and return its original acknowledgement. Persist idempotency independently of transient requests so a lost response or a resumed sync cannot duplicate the visit. Return a new Revision for each accepted change. Where visits share a chart, revisions must capture concurrent chart changes across visits.

## Connect a charting system

Start with the EHR's vendor-supported write capabilities and a sandbox. An EHR offering FHIR may support SMART Backend Services for practice-authorized API access. The EHR's available resources and write permissions determine which chart, order, and referral workflows the adapter can map. [HL7 SMART Backend Services](https://hl7.org/fhir/smart-app-launch/backend-services.html)

Confirm receipt only after the charting system has accepted the write, or the adapter has durably accepted responsibility for delivery and can surface downstream failures to the clinic. MedQueue displays adapter acknowledgement as such. Referral transmission and prescription routing are subject to the destination's authorized clinical workflow. Test check-in, edits, encounter completion, duplicate delivery after a lost response, and revision conflicts in a staging practice before real use.
