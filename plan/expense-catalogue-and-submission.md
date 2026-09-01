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
| Auth | one static `ExpenseApi:ApiKey` bearer | JustLogin system token + per-company token |
| Submission | `POST {BaseUrl}/expenses`, flat provisional JSON (risk R1) | the real JustLogin contract |

Deliberate difference from the Lambda: **the Lambda enriches an existing receipt; Justina originates
one from a chat message.** So Justina needs a *create* path, not only the Lambda's `update` path.
That is the one contract item still missing (see §7 O-1).

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

### S2 — Catalogue client + cache (Infrastructure) — *new files only*

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

---

## 7. Open items for the Product Owner

| Id | Question | Blocks |
|---|---|---|
| **O-1** | **Is there a create endpoint?** The Lambda only ever *updates* a receipt that already exists (`PUT expense/v1/Receipt/update`). Justina originates receipts from chat. Either (a) a `POST expense/v1/Receipt` exists — send its contract; or (b) Justina must first create a receipt shell (how?) and then update it. | S6, all of R1 |
| **O-2** | Base URL and environment for the Expense API (dev/sandbox), plus credentials | S2, S6, S7 |
| **O-3** | Token model for a non-AWS caller: does Justina get its own system credential in JustLogin Identity, and which SDK/endpoint issues it? | S7 |
| **O-4** | **Identity mapping.** A Telegram/WhatsApp user id is not a JustLogin user. How does Justina learn the organization GUID and the submitting user for a conversation — enrollment step, membership lookup by phone number, or configured mapping? | S2, S6, S7 — this is now the largest gap |
| **O-5** | Should Justina reuse the Lambda's prompt text verbatim (shared wording, one behaviour) or keep Justina's own instruction as the base and append only the catalogue rules? This plan assumes the latter. | S3 |
| **O-6** | Multi-currency: the Lambda returns `CurrencyCode`/`Symbol`/`Name`; Justina stores a single `Currency`. Add the other two, or keep one? | S5 |

Nothing in §4 gets implemented until O-1 and O-4 have answers, except S1, S3 and S8's prompt and
normalizer tests, which are contract-free and can start immediately.
