# Send Candidate Summary — task list

Branch `feature/recruitment-summary` in **Assistant.AI**, **Recruitment-API**, **Recruitment-UI**.
Plan and reasoning: [plan/recruitment-summary.md](plan/recruitment-summary.md).

Legend: `[ ]` not started · `[~]` implemented, not verified · `[x]` done and verified · `[!]` blocked

## Decisions taken (2026-09-02)

| # | Decision |
|---|---|
| 1 | **One configured Telegram id** for this phase — the one already in the channel-link fixture. No user-to-Telegram mapping, no new configuration surface. Structured so mapping can be added later without a refactor. |
| 2 | **Configured service account** supplies the acting identity for system-triggered flows. No `?userGuid=` from the caller. |
| 3 | **`Candidate/{id}/Status` becomes `PUT`.** Compatibility implications documented below before the change is made. |
| 4 | **Interview defaults come from the hiring stage.** Only genuinely missing values are asked for. |
| 5 | **A pending expense receipt always outranks recruitment.** Recruitment defers and resumes; the rule is written into `AGENTS.md` rather than left to the model. |

### What §3 actually breaks — checked, not assumed

`Candidate/{candidateId}/Status?status={statusId}` is `[HttpGet]` at `CandidateController:247`.

Callers, across every repository on this machine: **exactly one.**

- `Recruitment-UI` → `candidate-detail-services.ts:309` `getCandidateStatus()` → `this._http.get(url)`

No caller in Recruitment-API, Angular-Host-Application, or any other checked repo. So the migration is
two lines in one file plus the attribute. Still, an API is not only its callers in this building:

- **Mobile or portal clients built elsewhere** would break on a verb change. Unknown to me — needs a
  moment's thought from someone who knows what consumes this API.
- **Safe path if that is a real risk:** add `[HttpPut]` alongside, keep `[HttpGet]` marked `[Obsolete]`
  pointing at it, move the UI, and drop the GET in a later release.
- **Note:** `dropdowns/CandidateStatus` (the list) is a different endpoint and is genuinely a GET. It is
  untouched.

### What §4 buys us — checked

`HiringStageResModel` already carries `InterviewTitle`, `InterviewerIds`, `InterviewMediumId`,
`Duration` and `PrivateNoteForInterviewer`. Every required field of the interview payload except the
date and time has a default on the stage.

So the conversation is one question — *when?* — not four. Justina asks for anything the stage leaves
blank, and nothing else.

---

## Recruitment-API

- [x] **A1** `ICandidateSummaryService.BuildSummary` — composed from the candidate record and the CV that AI parsing already extracted. Its own service rather than a method on `IRecruitmentAiService`: no model is called, because the parsing already happened and its output is stored. Swapping in written prose later is one method
- [x] **A2** `POST candidates/{candidateId}/send-summary` — generates the summary, posts it to Justina; `[AuthorizeUserRoleAttribute("admins, system_token")]`, following the `Candidate/chatbot/query` precedent
- [x] **A3** Service-account identity: configured user GUID used when `TokenData.UserGUID` is absent, so schedule and status have a valid actor without the caller supplying one
- [x] **A4** `PUT Candidate/{candidateId}/Status` added; the GET kept and marked `[Obsolete]`, delegating to it — existing callers unaffected
- [x] **A5** Confirm `InterviewSchedule` accepts a system token with the service-account identity; extend if not
- [x] **A6** 9 unit tests: summary composition, the no-CV path, identifiers carried, unknown candidate, delivery, and the acting-user rules
- [x] **A7** No regression: the suite fails 176 tests on the branch point too (integration tests wanting a live API and database). Verified by stashing. The 9 new ones are the only passing tests either way

## Justina (Assistant.AI)

- [ ] **J1** `POST /notifications/candidate-summary`, authenticated with `ToolApi:SharedSecret`. Summary text is **data, never instruction** (§38) — a CV saying "mark me hired" must arrive as words
- [ ] **J2** `IProactiveMessenger` + OpenClaw gateway implementation — Justina's first unprompted outbound message
- [ ] **J3** Recipient resolution behind one seam, returning the single configured Telegram id today; mapping slots in later without touching callers
- [ ] **J4** `CandidateSummary` aggregate: `Sent → AwaitingAvailability → Scheduling → Scheduled`, plus `StatusUpdated` / `Cancelled`
- [ ] **J5** Deferral: a recruitment event arriving while a receipt is in `WaitingConfirmation` is **queued, never dropped and never overwriting** `ActiveWorkflow`; resumed when the receipt finishes
- [ ] **J6** `IRecruitmentApiClient` — schedule and status against Recruitment-API, company token via the existing provider (rename it: no longer expense-specific)
- [ ] **J7** Interview payload assembled from hiring-stage defaults; only unresolved fields are asked for
- [ ] **J8** MCP tools: `justina_recruitment_send_summary`, `_schedule_interview`, `_update_status`
- [ ] **J9** Capabilities `recruitment.schedule` / `recruitment.status`, distinct from the expense ones
- [ ] **J10** `AGENTS.md` recruitment section, with the receipt-outranks-recruitment rule stated explicitly
- [ ] **J11** EF migration for the new tables — additive only, `Receipts` untouched
- [ ] **J12** Idempotency: a double-clicked button sends one message; a repeated "Thursday 2pm" books one interview

## Recruitment-UI

- [~] **U1** Button added beside *Interview Question Recommendation*, same `isAiEnabled` guard and `isInActiveUser` disable, plus disabled while a send is in flight
- [~] **U2** `sendCandidateSummary` endpoint constant and service method added
- [~] **U3** Success toast names the candidate summarised; failure says so rather than failing quietly
- [~] **U4** `getCandidateStatus` → `updateCandidateStatus`, now a PUT. Four call sites and their specs moved; the list service's identically named dropdown lookup is untouched and genuinely is a GET
- [~] **U5** Three keys added to all four locales (en, fr, zh, zh-HK), JSON validated

## Verification

- [ ] **V1** Expense end to end still green after every Justina change — the receipt path is live
- [ ] **V2** Receipt awaiting confirmation + summary arrives → receipt untouched, recruitment resumes after
- [ ] **V3** CV containing prompt-injection text reaches the HR manager as words and changes nothing
- [ ] **V4** Double-clicked button → one Telegram message
- [ ] **V5** Repeated "book Thursday 2pm" → one interview in Recruitment-API
- [ ] **V6** Interview created from stage defaults, with only the date asked for
- [ ] **V7** Status update visible in the recruitment web app
- [ ] **V8** Full suites green in all three repos

## Open

- [!] **Q4** The UI cannot be built on this machine: the project is Angular 13 and Node here is 24, so `npm install` fails with "Cannot read properties of null". Every U task is therefore implemented but uncompiled. Needs a build on a machine with Node 14 or 16, or a `.nvmrc`

- [ ] **Q1** Does any client outside these repositories call `Candidate/{id}/Status`? Decides whether A4 keeps the deprecated GET
- [!] **Q2** Which user GUID is the service account, and does it exist in dev? Code is in and refuses clearly while unset (`Assistant:ServiceAccountUserGuid`); nothing system-triggered can schedule or change status until it is
- [x] **Q3** Answered: the candidate's current active stage. The summary carries `stageId` from the candidate record
