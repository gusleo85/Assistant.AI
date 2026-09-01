# Plan — Expense catalogue (categories + taxes) and submission to the Expense API

Status: **DRAFT — awaiting Product Owner approval. No code has been written for this plan.**
Companion to `plan/task.md`; it does not replace it. Everything here is **additive**: no existing
Justina class is rewritten, no existing signature is removed, no existing migration is edited.

Source of truth for the contract: `C:\git\ReceiptScannerLambda`
(`JustLogin.ReceiptScanner.Lambda`), read on 2026-09-01.

---

## 1. What the Lambda actually does

The Lambda is not a chat product. It is an S3-triggered enricher for a receipt that **already exists**
in the JustLogin Expense system.

| # | Step | Where |
|---|---|---|
| 1 | S3 object lands; the object key **is** the receipt GUID (`{guid}.{ext}`) | `Function.cs` |
| 2 | `GenerateSystemToken()` → system bearer token | `Services/ExpenseService.cs` |
| 3 | `GET expense/v1/Receipt/{receiptId}` with the **system** token → `OrganizationId` | `GetAttachmentDetailsAsync` |
| 4 | Membership API `GetCompanyAsync(companyGuid)` → `CompanyId` | `Function.cs` |
| 5 | `SetCompanyToken(organizationGuid)` → `GetCompanySystemToken` → **company** bearer token | `ExpenseService.cs` |
| 6 | `GET expense/v1/Categories?isActive=true&includeDefault=true` with the company token | `GetCategories` |
| 7 | `GET expense/v1/Taxes` with the company token | `GetTaxes` |
| 8 | Category **names** and tax **labels** are joined with `", "` and formatted into the user prompt | `Function.cs:148-156` |
| 9 | OpenAI Vision returns JSON constrained to those lists | `Vision/VisionService.cs` |
| 10 | Returned `Category` name is resolved back to `CategoryId`; returned `Taxes` labels back to `TaxIds` | `Function.cs:173-184` |
| 11 | `PUT expense/v1/Receipt/update` with the enriched `ReceiptRequest` | `UpdateAttachmentDetailAsync` |

### The prompt injection mechanism (what the user asked about)

`src/JustLogin.ReceiptScanner.Lambda/Configs/appsettings.json` holds two prompt strings:

* `OpenAI:SystemRequest` — fixed extraction rules (date, currency inference, output fields).
* `OpenAI:UserRequest` — a **`string.Format` template with two placeholders**:

```
"Category must be one of: {0}. Prefer the closest semantic match ... use Uncategorized only if nothing
 fits. ... Taxes: receipt not from Singapore -> []. If from Singapore, each entry must match a
 predefined tax in {1}. SG GST=9.00% ..."
```

Filled in `Helpers/OpenAIVisionConfigurationHelper.cs`:

```csharp
public static VisionUserRequest GenerateVisionUserRequest(
    this ReceiptScannerOpenAIVisionConfiguration openAIVisionConfiguration,
    string categories, string taxes = "")
{
    var text = string.Format(openAIVisionConfiguration.UserRequest,
        categories, string.IsNullOrEmpty(taxes) ? "" : taxes);
    ...
}
```

`{0}` = `string.Join(", ", categories.Select(x => x.Name))`
`{1}` = `string.Join(", ", taxes.Select(x => x.NameAndRate))`

### The two catalogue DTOs, verbatim

```csharp
public class ExpenseCategoryResponse
{
    public string Id { get; set; }            // GUID as string
    public string Name { get; set; }          // what goes in the prompt
    public string AccountCode { get; set; }
    public string Description { get; set; }
    public bool IsActive { get; set; }
    public string CompanyGuid { get; set; }
}

public class ExpenseTaxesResponse
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public decimal Rate { get; set; }
    public string NameAndRate => string.Concat(Name, " (", Rate, "%)");  // e.g. "GST (9.00%)"
    public Guid OrganizationId { get; set; }
    ...
}
```

**`NameAndRate` is the join key in both directions** — it is what the model is shown and what the
model's answer is matched against (`content.Taxes.Contains(x.NameAndRate)`). Its exact rendering
depends on the decimal scale the API returns (`9.00` vs `9`). Justina must build the label from the
API's own value verbatim, never re-format it.

### The enriched payload sent back

```csharp
public class ReceiptRequest
{
    public string ReceiptId, MerchantName, ReferenceNumber, Date, Amount;
    public string CurrencyCode, CurrencySymbol, CurrencyName;
    public string Category;  public Guid? CategoryId;
    public string Location;
    public List<string> Taxes;  public List<Guid> TaxIds;
}
```

Endpoints, all relative to `JLHttpClient:BaseUrl` (no leading slash), defaults in
`Helpers/ExpenseApiHelper.cs`:

| Config key | Default | Token |
|---|---|---|
| `ExpenseApi:GetCategoriesEndpoint` | `expense/v1/Categories` | company |
| `ExpenseApi:GetTaxesEndpoint` | `expense/v1/Taxes` | company |
| `ExpenseApi:GetReceiptDetailEndpoint` | `expense/v1/Receipt` | **system** |
| `ExpenseApi:UpdateReceiptEndpoint` | `expense/v1/Receipt/update` | company |

Environment base URLs: dev `https://apis.justlogindevelopment.xyz`, staging
`https://apis.justloginstaging.xyz`, prod `https://apis.justlogin.com`.

### Auth — OAuth2 client_credentials, two tokens, tenancy inside the JWT

No API key, no tenant header. Two tokens from the same endpoint
(`IdentitySDK:TokenEndpoint`, default `v1/auth/connect/token`, form-urlencoded):

1. **System token** — `client_id`, `client_secret`, `grant_type=client_credentials`, `scope`.
   Cached until expiry (`SingletonAuthenticationClient.GenerateToken`).
2. **Company token** — the same call **plus a non-standard `CompanyID` form field**. That extra field
   is the entire tenancy mechanism: it mints a company-scoped JWT. Not cached in the Lambda — a fresh
   one per invocation (`AuthenticationClient.GetCompanySystemToken`).

Three distinct tenant identifiers, and the differences matter:

| Identifier | Format | Where from | Used for |
|---|---|---|---|
| `organizationId` | `Guid`, dashed | `GET expense/v1/Receipt/{id}` response | tenant key inside expense-api |
| `companyGuid` | 32-char **dashless UPPERCASE** (`ToString("N").ToUpper()`) | derived from `organizationId` | path segment of `membership/v2/companies/{companyGuid}`; the SDK's `CompanyGuid` type throws unless length is exactly 32 |
| `CompanyId` | `string`, a separate legacy id | membership API response | the `CompanyID` form field on the token request |

```
receipt GUID → GET expense/v1/Receipt/{guid} [system]
             → organizationId → companyGuid ("N", upper)
             → GET membership/v2/companies/{companyGuid} [system] → CompanyId
             → POST v1/auth/connect/token { grant_type, CompanyID, client_id, client_secret, scope }
             → company JWT → Categories / Taxes / Receipt update
```

**The PUT body carries no organization, company, member or user id.** All tenancy rides in the bearer.
The Lambda acts as a service principal; `MemberId` and `CreatedBy` come back on the GET and are never
read or forwarded.

### Wire-format details that will bite on reimplementation

* The update body is serialized with `System.Text.Json` **default options and no
  `[JsonPropertyName]`** → property names go out **verbatim PascalCase** (`MerchantName`, `TaxIds`),
  and nulls are included. The *responses*, by contrast, are read with `ReadFromJsonAsync`
  (case-insensitive), so the APIs themselves return camelCase.
* `Amount` and `Date` are **strings**, not numbers. `Date` is `yyyy-MM-dd` or `""` — anything that
  fails `TryParseExact` is silently blanked by the DTO setter.
* Category matching is `x.Name == content.Category` — **ordinal, case-sensitive, untrimmed**. No match
  → `CategoryId` null while the free-text `Category` still goes out.
* Response to both GET and PUT is `ExpenseReceiptResponse`: `status`, `type`, `memberId`, `expenseId`,
  `reportId`, `createdDate/By`, `updatedDate/By`, `organizationId`. **No receipt id, none of the
  extracted fields.**
* Errors: no structured error body anywhere. Non-2xx → status code + reason phrase only; the body is
  logged, never parsed. No retry on any expense-API call (Polly is configured but only Vision opts in).
  Failures `continue` to the next S3 record — silently dropped, no DLQ.

---

## 2. The gap on the Justina side

| Concern | Justina today | Needed |
|---|---|---|
| Category | `RawReceipt.Category` → free string, straight to `Receipt.Category` | constrained to the company's catalogue; resolved to `CategoryId` |
| Tax | single `TaxAmount` decimal | tax **codes**: matched labels → `TaxIds` |
| Prompt | `ReceiptExtractionSchema.Instruction` is a `const` with no placeholders | a composed instruction carrying the catalogue |
| Tenancy | conversation identity only (`channel`, `userId`, `conversationId`) | an organization/company GUID per conversation |
| Auth | one static `ExpenseApi:ApiKey` bearer | OAuth2 `client_credentials`: a system token, plus a company-scoped token minted with a `CompanyID` form field |
| Submission | `POST {BaseUrl}/expenses`, flat provisional JSON (risk R1) | the real JustLogin contract |

Deliberate difference from the Lambda: **the Lambda enriches an existing receipt; Justina originates
one from a chat message.** So Justina needs a *create* path, not only the Lambda's `update` path.
That is the one contract item still missing (see §7 O-1).

---

## 1b. What `C:\git\expense-api` actually offers (read 2026-09-01)

Base path is `/expense` (`Program.cs` `UsePathBase`, `ENV BasePath=expense`), routes are
`/expense/v1/{Controller}`, responses are camelCase, and request binding is case-insensitive.

### There is no JSON create endpoint for a receipt — and the create path triggers the lambda itself

`POST /expense/v1/Receipt/scan` (multipart, field `file`, images only, ≤20 MB) is the create path. In
`AttachmentService.ScanAsync` it creates an `Expenses` row and an `Attachment` row, then **uploads the
file to both buckets itself**:

```csharp
// save file to storage (to trigger receipt scan lambda)
await _s3Service.UploadFileAsync(stream, filename, _awsBucketConfig.ReceiptBucketName, ...);   // {id}.jpeg
// save compressed file to attachment bucket
await _s3Service.UploadFileAsync(compressedFile, entity.Id.ToString(), _awsBucketConfig.AttachmentBucketName, ...);
```

So **choosing a bucket is not Justina's to make on this path**: calling `/Receipt/scan` puts the image
in the receipt-scan bucket and the scanner lambda runs, whatever Justina does next. And the two
extractions cannot both land — `ReceiptService.UpdateReceiptAsync` refuses any attachment not in
`ScanInProgress` or `UnReadable`, and the lambda's update flips it to `ScanComplete`. Whichever writer
is second is **rejected**, and on the Justina side that would surface as a failed submission of a
receipt the user already confirmed.

### The path that avoids all of it

`POST /expense/v1/Expenses` with `EditExpenseRequest` creates an expense directly, carrying
`CategoryId`, `TaxIds`, `Amount`, `Date`, `CurrencyId`, `MerchantName`, `Location`,
`ReferenceNumber`, `OrganizationId` and `SubmitterId` — and it takes the organization and submitter
**from the body**, not from the token. The image then goes through
`POST /expense/v1/Expenses/{id}/receipt/upload`, which writes to the attachment bucket only and never
touches the receipt bucket. One extractor, no race, no status gate. **This is the path Justina should
take**, and it makes the whole scan/update contract irrelevant to us.

### Other facts that settle open questions

| Question | Answer |
|---|---|
| Does a system token work for Categories/Taxes? | Yes. `GetOrganizationId()` falls back to a **`?companyGuid=` query parameter** when the token's `CompanyGUID` claim is blank and the token is the System token. `GET /Categories/list/{organizationId}` and `/Taxes/list/{organizationId}` take the org in the route and never read the token at all. **No company-token dance is required.** |
| Which claims carry tenancy | `CompanyGUID` and `UserGUID` (plus `CompanyID`, `CompanyName`, `CountryCode`, `Username`, `WorkEmail`, `role`) |
| Can a member belong to several companies? | No. `Member.Id` is the `UserGUID` and is the primary key; `OrganizationId` is a single scalar FK. D6 holds. |
| Phone → member? | **Does not exist anywhere in expense-api.** Email is returned on member DTOs but is never searchable. This must come from the membership service. |
| Category id format | `CategoryResponse.Id` is an **uppercase GUID string** (`ToUpperGuidString()`), while `/Categories/list/...` returns lowercase. Parse to `Guid`; never compare as strings. |
| `Taxes` on the update DTO | Does not exist — `TaxIds: Guid[]` only. |
| Enums on the wire | Integers. No `JsonStringEnumConverter` is registered. |

---

## 2a. Decisions taken (2026-09-01)

| # | Decision |
|---|---|
| D1 | **Justina stays on `net10.0`.** No downgrade to net6 — that would mean regressing EF Core 10, Serilog 10 and OpenTelemetry, regenerating migrations, rewriting net9+ APIs already in use (`Convert.ToHexStringLower`), and running on a runtime out of security support since November 2024. |
| D2 | **`JustLogin.Identity.SDK` is copied, not modified.** Its `.cs` files go into `src/Justina.JustLogin.Identity/` byte-identical. The only difference from upstream is the copied `.csproj`: `net10.0` instead of `net6.0`, plus `TreatWarningsAsErrors=false` (the SDK has nullable/unused warnings that Justina's build treats as errors). A header comment records the source repo, commit and the fact that the source must not be edited locally. |
| D3 | **The MyGet credential never enters git.** `nuget.config` is committed with a placeholder feed URL; the real `https://www.myget.org/F/justlogin/auth/<token>/api/v3/index.json` comes from an environment variable locally and a CI/Docker build secret in the pipeline. Committing it verbatim would contradict `docs/03-qa/security-testing.md`. |
| D4 | **A `Stub`/`Live` mode flag covers catalogue, submission and identity.** `ExpenseApi:Mode`, default `Stub`. Until credentials and the create-endpoint contract arrive, the whole chat journey — receive image, extract, show, edit, confirm — runs end to end with no JustLogin dependency. Each of the three seams flips to `Live` independently. |
| D5 | **Media does go to S3, but never to the triggered bucket.** The flow is: OpenClaw receives the photo → Justina **creates the receipt record** in expense-api and gets a receipt id back → Justina uploads the image under a generated GUID to the **attachment** bucket. It must not land in `xyz.justlogindevelopment.receipt-scan`, whose `ObjectCreated` event fires `ReceiptScannerProcessingLambda`: that lambda would run a second OpenAI scan and PUT its own result over whatever the user confirmed in chat. **Justina does its own extraction; exactly one extractor per receipt.** ⚠ In the *lambda's* config `AWSBucketConfig:AttachmentBucketName` is itself set to the receipt-scan bucket — the physical bucket names and which one carries the trigger must be confirmed against `expense-api` before any upload code is written. |
| D6 | **One member, one company.** Resolution is by WhatsApp phone number; the "loop" is just over lookup results and the first match wins. Multi-company membership is out of scope until proven otherwise. |
| D7 | **Telegram needs a link step.** A Telegram update carries a numeric user id and an optional `@username`, not a phone number. A one-time link maps that id to a JustLogin member, and Justina stores the mapping in its own database. Stub mode uses a configured member. |

### Rails on Stub mode

Stub mode must never be mistakable for a working integration:

* startup logs a warning naming the mode, and `/health/ready` reports it;
* **`Stub` + `ASPNETCORE_ENVIRONMENT=Production` fails fast at startup** rather than faking submissions
  to real users;
* the chat confirmation says the receipt was *recorded*, not that it reached the expense system;
* `test/test-report.md` counts nothing exercised under Stub as integration-verified.

---

## 3. Design principles for this slice

1. **Additive only.** Every existing public member keeps its current signature; new behaviour arrives
   through overloads and new types. `ReceiptExtractionSchema.Instruction` stays exactly as it is and
   becomes the *base* of the composed prompt — its 21 existing tests keep passing untouched.
2. **The catalogue never blocks extraction.** If `GET Categories` fails, extraction proceeds
   unconstrained, exactly as today, and the receipt is marked as having an unresolved category. A
   downstream outage must not cost the user their receipt.
3. **Catalogue text is sanitized before it enters a prompt.** The values come from our own API, but
   §38 says instructions are never assembled from data. Names are stripped of newlines, control
   characters and braces, length-capped, and count-capped before formatting.
4. **Ids never come from the model.** The model only ever returns names/labels; C# resolves them to
   GUIDs against the catalogue it fetched. An unmatched name yields no id — never a guessed one.
5. **Cache is keyed by organization.** A cache that leaks one company's categories into another
   company's prompt is a tenancy breach, not a performance bug.

---

## 4. Work slices

Slices **S0–S4 and S8 run entirely under Stub mode** and need no JustLogin credentials, no create
endpoint and no membership contract. S5–S7 wait on the open items in §7.

### S0 — JustLogin SDK copy + package feed — *new project, nothing existing touched*

* `src/Justina.JustLogin.Identity/` — the SDK's `.cs` files, byte-identical (D2). Not added to
  `Justina.slnx`'s architecture-test scope; it is third-party code we host, not Justina code.
* `nuget.config` at the repo root with the placeholder feed (D3), `%JL_NUGET_FEED%`-style
  substitution documented in `docs/02-developer/getting-started.md`.
* `src/Justina.Api/Dockerfile` gains a `--mount=type=secret` for the feed URL on the restore layer,
  so the credential never lands in an image layer.
* Packages this drags in: `JustLogin.SDK.Core 1.0.0`, `JustLogin.Membership.SDK 1.0.2`,
  `Justlogin.Configurations.HttpClient 1.0.2`, `Amazon.Extensions.Configuration.SystemsManager 4.0.0`,
  `System.IdentityModel.Tokens.Jwt 6.29.0`. **Verify each restores and runs on net10 before anything
  is built on top of them** — this is the first thing S0 does, and if one of them is net6-only the
  D1/D2 decision has to be revisited.
* The SDK's config comes from AWS SSM (`/identityserver`) in the Lambda. Justina runs in Docker with
  no AWS, so `IdentitySDK__ClientID`, `__ClientSecret`, `__Scope`, `JLHttpClient__BaseUrl` come from
  `.env`. Under Stub mode none of them is required.

### S1 — Catalogue contract (Application) — *new files only*

`src/Justina.Expense.Application/Abstractions/IExpenseCatalogue.cs`

```csharp
public sealed record ExpenseCategory(Guid Id, string Name, string? AccountCode, bool IsActive);
public sealed record ExpenseTax(Guid Id, string Name, decimal Rate, string Label);
public sealed record ExpenseCatalogue(
    IReadOnlyList<ExpenseCategory> Categories,
    IReadOnlyList<ExpenseTax> Taxes)
{
    public static readonly ExpenseCatalogue Empty = new([], []);
    public bool IsEmpty => Categories.Count == 0 && Taxes.Count == 0;
}

public interface IExpenseCatalogue
{
    Task<Result<ExpenseCatalogue>> GetAsync(OrganizationRef organization, CancellationToken ct);
}
```

`Label` is stored, not computed, so it is byte-identical to what the API returned.

### S2 — Catalogue: stub + live client + cache (Infrastructure) — *new files only*

* `Api/StubExpenseCatalogue.cs` — a fixed JustLogin-shaped list (Meals and Entertainment, Medical
  Expense, Medicine Purchase, Accommodation Expense, travel categories, plus `GST (9.00%)` with a
  stable GUID per entry). Registered when `ExpenseApi:Mode` is `Stub` (D4). This is what makes the
  prompt-injection work testable today.
* `Api/ExpenseCatalogueClient.cs`, typed `HttpClient`, reusing `ExpenseApiOptions` with three new
  optional properties: `CategoriesPath` (default `expense/v1/Categories`), `TaxesPath`
  (default `expense/v1/Taxes`), `CategoriesQuery` (default `isActive=true&includeDefault=true`).
  Existing `ExpenseApiOptions` members are untouched.
* `Api/CachingExpenseCatalogue.cs` — decorator over the client, `IMemoryCache`, TTL from
  `ExpenseApi:CatalogueCacheMinutes` (default 10), key `catalogue:{organizationId}`. The Lambda
  refetches per event because it is short-lived; Justina is long-lived and must not hammer the API on
  every photo.
* Failure → `Result.Success(ExpenseCatalogue.Empty)` plus a warning log. Extraction degrades, it does
  not fail.

### S3 — Prompt composition — *new file; schema file gains fields only*

`src/Justina.Expense.Application/Receipts/ReceiptExtractionPrompt.cs`

```csharp
public static string Compose(ExpenseCatalogue catalogue)  // Empty => returns Instruction unchanged
```

Appends, after the existing instruction, a block modelled on the Lambda's `UserRequest`:

* category list, `", "`-joined, "prefer the closest semantic match; use no category if nothing fits";
* tax labels, `", "`-joined, plus the Singapore GST rules (9% now, 7% pre-2024, 8% during 2023,
  derive the rate from `amount / (subtotal + service charge)` when only an amount is printed);
* non-Singapore receipts → empty tax list.

Sanitization, applied to every name and label before it is joined: strip `\r\n\t` and other control
characters, collapse whitespace, drop `{`/`}`, trim to 80 chars, cap at 200 categories / 50 taxes.
Over the cap, the catalogue is treated as unusable for constraint purposes (fall back to unconstrained
plus a warning) rather than silently truncated.

`ReceiptExtractionSchema.Json` gains one field — `"taxes": { "type": "array", "items": {"type":
["string","null"]} }` — added to `required`, since `additionalProperties` is `false`. `taxAmount`
stays: the printed tax *amount* and the matched tax *codes* are different facts and both are wanted.
`RawReceipt` gains `IReadOnlyList<string>? Taxes`.

### S4 — Resolution (normalizer) — *overload, existing signature preserved*

```csharp
public static NormalizedReceipt Normalize(RawReceipt raw)                        // unchanged, delegates
public static NormalizedReceipt Normalize(RawReceipt raw, ExpenseCatalogue cat)  // new
```

* category: exact match on trimmed, case-insensitive name → `CategoryId`; no match → keep the name,
  leave the id null;
* taxes: each returned label matched (trimmed, case-insensitive) against `ExpenseTax.Label` →
  distinct `TaxIds`; unmatched labels are dropped and counted in a log field.

### S5 — Domain + persistence — *additive columns, new migration*

`Receipt` gains `CategoryId (Guid?)`, `TaxIds (IReadOnlyList<Guid>)`, `Location (string?)`.
`ReceiptField` gains `Location`. Editing `Category` by name re-resolves the id through the catalogue
and clears it when the new name is not in the catalogue — an edit must never leave a name and a
contradicting id.

New migration `AddReceiptCategoryIdAndTaxes`: nullable columns plus a `ReceiptTax` child table (or a
JSON column — decide at implementation time; the child table is preferred for queryability).
No existing migration is modified.

### S6 — Submission mapping

`ExpenseSubmission` gains `Guid? CategoryId`, `IReadOnlyList<Guid> TaxIds`, `string? Location`,
`OrganizationRef Organization`. Only `ExpenseApiClient.BuildPayload` and `ReadExpenseId` change —
which is exactly the isolation `plan/task.md` §31 promised for R1.

Payload field names follow `ReceiptRequest` above (`merchantName`, `referenceNumber`, `date`,
`amount`, `currencyCode`, `currencySymbol`, `currencyName`, `category`, `categoryId`, `location`,
`taxes`, `taxIds`), pending the creation-endpoint answer in §7 O-1.

### S7 — Auth and tenancy

`IExpenseTokenProvider` in Application; an Infrastructure implementation that mirrors the Lambda's
two-token model (system token; per-company token via the company GUID) and caches each until shortly
before expiry. `ExpenseApiClient` and `ExpenseCatalogueClient` both take their bearer from it. The
current static `ExpenseApi:ApiKey` path stays as the fallback so nothing that works today breaks.

### S8 — Tests (no existing test file rewritten)

* Prompt: catalogue names appear; injection attempts inside a category name are neutralised; empty
  catalogue produces the byte-identical current instruction; over-cap catalogue falls back.
* Normalizer: name→id, label→ids, unmatched name keeps text and no id, case/whitespace tolerance.
* Catalogue client: WireMock for both endpoints, 401/500/timeout → `Empty`, cache hit/miss/TTL,
  two organizations never share a cache entry.
* Submission: payload carries `categoryId` and `taxIds`; a receipt with an unresolved category still
  submits with the name only.
* Architecture: `Justina.Expense.Application` still has no HTTP dependency.

---

## 5. Sequence after this slice

```
media received → document processed
              → catalogue fetched for the conversation's organization (cached)
              → prompt composed = base instruction + category list + tax list
              → Vision returns names/labels only
              → C# resolves names/labels to CategoryId / TaxIds
              → user confirms in chat
              → submission carries ids, not guesses
```

---

## 6. Risks

| Id | Risk | Mitigation |
|---|---|---|
| C1 | Tax label rendering differs from the API's (`9.00%` vs `9%`) and nothing ever matches | store the API's own label verbatim; test both scales |
| C2 | A large catalogue inflates every prompt and its cost | count/length caps; fall back to unconstrained above the cap |
| C3 | Stale cache after a category is renamed or deactivated | short TTL; invalidate on a resolution miss; never cache an error |
| C4 | Two categories with the same name | resolve to the first active match and log the ambiguity; PO decides the tiebreak |
| C5 | Catalogue of company A used for company B | cache key is the organization GUID; a test asserts isolation |
| C6 | A malicious category name carries an instruction | sanitize + cap (stricter than the Lambda) |
| C7 | The Lambda matches category names **case-sensitively and untrimmed**, so `"meals and entertainment"` silently loses its id. Justina matching case-insensitively means the two systems can disagree on the same receipt | match case-insensitively and log every case/whitespace-only difference, so the divergence is visible rather than silent |
| C8 | No structured error body and no retry exists on the JustLogin side; a 500 is indistinguishable from a validation refusal | Justina keeps its own `ErrorCodes` mapping by status code, as `ExpenseApiClient` already does, and keeps the receipt retryable |

---

## 7. Open items for the Product Owner

| Id | Question | Blocks |
|---|---|---|
| **O-1** | **Is there a create endpoint?** Confirmed: the Lambda has **no create call at all**. The receipt record already exists — whatever uploads to S3 creates it, and the S3 object key *is* the receipt GUID. Justina originates receipts from chat, so it needs either (a) a `POST expense/v1/Receipt` — send its contract; or (b) the same upload path the Lambda's producer uses, so Justina creates the shell and then updates it. | S6, all of R1 |
| **O-2** | Which environment does Justina target (`apis.justlogindevelopment.xyz` / staging / prod), and does Justina get its own `IdentitySDK:ClientID` / `ClientSecret` / `Scope`? | S2, S6, S7 |
| **O-3** | Confirmed mechanism: company-scoped JWT from `client_credentials` + a `CompanyID` form field. Open part — is that extra field available to a non-AWS caller with Justina's own credential, and what scope must Justina request? | S7 |
| **O-4** | **Identity mapping — approach decided (D6), contract missing.** Resolution is by phone number or email against the membership API. The Lambda contains no such endpoint: it only has `membership/v2/companies/{companyGuid}` (GUID → company), because it derives the organization from a receipt that already exists. **Need the path, request and response of the endpoint that maps a phone number or email to a member and their organization.** | S7 Live only — Stub uses a configured org/member |
| **O-7** | Should Justina reuse the Lambda's PascalCase, all-strings wire format verbatim (`Amount`/`Date` as strings), or does the create endpoint take a cleaner typed contract? | S6 |
| **O-9** | **Orphan receipts.** Creating the record on image-received (D5) means a record exists before the user has confirmed anything — and Justina's state machine lets a user cancel, or simply walk away. Does expense-api support deleting or voiding a receipt, and what should Justina call when a user cancels in chat? Without it, every abandoned conversation leaves a stray receipt. | S6 |
| **O-10** | **Which bucket actually carries the lambda trigger**, and what is the attachment bucket's real name per environment? The lambda's own `AttachmentBucketName` points at the receipt-scan bucket, so the names alone cannot be trusted. | S6 upload |
| **O-8** | The Lambda restricts taxes to **Singapore GST only** (non-SG → `[]`). Does Justina keep that rule, or extract taxes for every locale? | S3 |
| **O-5** | Should Justina reuse the Lambda's prompt text verbatim (shared wording, one behaviour) or keep Justina's own instruction as the base and append only the catalogue rules? This plan assumes the latter. | S3 |
| **O-6** | Multi-currency: the Lambda returns `CurrencyCode`/`Symbol`/`Name`; Justina stores a single `Currency`. Add the other two, or keep one? | S5 |

**None of these block the first slice any more.** Under Stub mode (D4), S0–S4 and S8 are all
contract-free: the full journey — image in, extracted, category and taxes constrained to a catalogue,
shown, edited, confirmed — runs with no JustLogin credentials at all. O-1 and O-4 gate only the flip
to `Live`, i.e. S5–S7.

Two things are needed from the Product Owner to reach `Live`:

1. the **create endpoint** contract (O-1) — there is nothing in the Lambda to copy;
2. the **membership lookup by phone/email** contract (O-4).

O-2 (environment + Justina's own `IdentitySDK` client credential) is needed at the same time.
