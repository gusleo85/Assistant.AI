# JustLogin.Identity.SDK in Justina — analysis before work

Branch: `feat/justlogin-identity-sdk`. Nothing implemented yet; this is the analysis the work waits on.

## 1. What the SDK is

A small first-party client for the JustLogin identity server, vendored by source copy into every
consuming repository rather than published as a package. Three copies exist:

| Repository | TFM | JustLogin.SDK.Core | JWT |
|---|---|---|---|
| `ReceiptScannerLambda` | net6.0 | 1.0.0 | 6.29.0 |
| `ReimbursementEventIntegrationLambda` | net8.0 | 1.0.2 | 8.1.0 |
| `JustLogin.NotificationPDFGenerator.Lambda` | net8.0 | 1.0.2 | 8.1.0 |

The two net8.0 copies are byte-identical apart from csproj indentation. The Lambda copy is a generation
behind and carries an older `SdkResponse.Create` overload. **Take the net8.0 source.**

Public surface, all of it:

```csharp
Task<GetAuthenticationResponse> GetSystemToken(CancellationToken)
Task<GetAuthenticationResponse> GetCompanySystemToken(CompanyGuid, CancellationToken)
Task<SdkResponse<GetAuthenticationResponse>> GenerateToken(Dictionary<string,string>, CancellationToken)
Task<SdkResponse<GetAuthenticationResponse>> GenerateExchangeToken(ExchangeTokenGuid, CancellationToken)
```

`GetCompanySystemToken` is the one Justina needs. It resolves the company through
`membership/v2/companies/{companyGuid}` using the system token, then requests a second token carrying
that `CompanyID`. That company token is the Bearer credential the Expense API expects — exactly what
`ReceiptScannerLambda/Services/ExpenseService.cs:49` does before every catalogue and expense call.

`TokenDecoderHelper` decodes a JustLogin JWT into `CompanyGUID`, `CompanyID`, `UserGUID`, `Username`
and roles. Potentially useful later; not needed for submission.

## 2. Why Justina needs it

Everything identity-shaped in Justina is currently mocked: `StubExpenseTenantResolver`,
`MockExpenseEndpoints`, and an `ExpenseApi:ApiKey` static string that no real environment will accept.
The SDK is what turns `ExpenseApi:Mode=Stub` into a real integration, and it closes plan risk R1's
authentication half (the payload contract is still separately unknown).

The fit is good at the seam that matters. `ExpenseTenant.CompanyGuid` already renders the 32-character
uppercase form, and `CompanyGuid`'s constructor demands exactly that — the same invariant, arrived at
independently. So:

```
ExpenseTenant.CompanyGuid  ->  GetCompanySystemToken(...)  ->  Bearer token  ->  Expense API
```

## 3. Where it plugs in

- **New project** `src/JustLogin.Identity.SDK/` — vendored, its own csproj, added to `Justina.slnx`.
- **Referenced only by** `Justina.Expense.Infrastructure`. Nothing else may see it: the architecture
  tests already forbid Domain and Application depending on infrastructure, and SDK types are
  infrastructure by definition.
- **New abstraction** in `Justina.Expense.Application/Abstractions` — something like
  `IExpenseAccessTokenProvider.GetAsync(ExpenseTenant, CancellationToken)` returning `Result<string>`.
  The SDK stays entirely behind it. This is the same treatment `IExpenseApiClient` already gets, and it
  is what keeps a vendored third-party type out of our command handlers.
- **A `DelegatingHandler`** on the Expense API `HttpClient` that attaches the company token, replacing
  the static `ApiKey`/`ApiKeyPrefix` path in `ExpenseApiClient`.
- **Configuration** — an `IdentitySDK` section: `TokenEndpoint`, `ClientID`, `ClientSecret`, `Scope`,
  `MembershipCompanyEndpoint`. Secrets by environment variable, never committed.

## 4. What is wrong with the SDK as it stands

Found by reading it, all confirmed in the source. These are the reasons this is not a copy-and-build job.

**a. It logs the client secret.** `SingletonAuthenticationClient.GenerateToken` does:

```csharp
_logger.LogInformation("TokenService: Token API {TokenEndpoint} Request is {@IdentityFormCollection}", ...)
```

The dictionary destructured there contains `client_id`, `client_secret` and `scope`. At Information
level, into whatever sink is configured. Justina scrubs secrets deliberately (`SecretScrubber`,
`RemoveAllLoggers`, OTel URL scrubbing) after the Telegram token nearly leaked the same way. Copying
this in as-is would undo that on day one. **Must be removed in our copy** — not a preference.

**b. It will not compile under our build settings.** `Directory.Build.props` sets
`TreatWarningsAsErrors=true` with nullable enabled repo-wide. The SDK has uninitialised non-nullable
properties (`IdentityConfiguration.TokenEndpoint`), nullable-oblivious fields (`_systemToken`), and
unused locals. Either the vendored csproj relaxes those settings for itself, or the source gets edited.
Relaxing is the honest choice: edits we make to vendor code are edits we own forever.

**c. It drags in AWS.** `ParameterStoreHelper` pulls `Amazon.Extensions.Configuration.SystemsManager`
to read `/identityserver` from AWS Systems Manager. Justina runs in Docker against configuration and
environment variables. That file and that package reference have no purpose here.

**d. Company tokens are not cached.** The caching in `GetCompanySystemToken` is commented out, so every
call performs a membership lookup *and* a token request — two round trips per expense submission,
per catalogue fetch. The system token is cached (singleton, 5-minute early expiry); the company token
is not. Justina should cache per company with the same early-expiry rule.

**e. `GenerateSystemTokenOnStartup` blocks on `.Result`.** Sync-over-async at startup. We should not
call it; if we want a warm token, do it in a hosted service properly.

**f. It throws for expected failures.** `GenerateSystemTokenException` on a bad response. Justina's
convention is `Result<T>` for anything expected and exceptions only for defects. The wrapper translates.

**g. Private feed at Docker build time.** `JustLogin.SDK.Core` → `Justlogin.Configurations.HttpClient`
come from `https://www.myget.org/F/justlogin/auth/<guid>/api/v3/index.json`. All three are in the local
NuGet cache, so a host build works today — but `src/Justina.Api/Dockerfile:18` runs `dotnet restore`
*inside the container*, where that cache does not exist. **Without a `nuget.config` reachable from the
build, the Docker image will not build.** And that feed URL embeds an auth GUID, so it is a credential:
the other repositories commit it in plain text; we should not copy that habit.

**h. Dead code.** `AuthenticationClient._systemToken` and `._companySystemTokens` are never read.

## 5. Options

| | Approach | Cost | Risk |
|---|---|---|---|
| **A** | Vendor all 25 files unchanged, wrap it | Lowest now | Ships (a) the secret logging and (c) the AWS dependency |
| **B** | Vendor trimmed — drop `Startup/Helpers/ParameterStoreHelper.cs`, drop the AWS package, remove the secret log, keep everything else verbatim | One afternoon | Small, documented divergence from upstream |
| **C** | Don't vendor; write a ~150-line token client against the same endpoints | Two days | We own the identity contract, and drift when the real SDK changes |

**Recommend B.** A is not acceptable while it logs a client secret, and C throws away a first-party
contract that three other services already depend on. B keeps the public surface identical — a future
`git diff` against the upstream copy stays readable — and the three removals are each defensible in a
sentence.

## 6. Proposed steps

1. Copy the net8.0 source into `src/JustLogin.Identity.SDK/`, minus `bin`/`obj` and minus
   `ParameterStoreHelper.cs`; add to `Justina.slnx`; keep `net8.0` and relax the strict build settings
   in its own csproj.
2. Remove the secret-logging line. Leave a comment saying why, so nobody restores it from upstream.
3. Add `nuget.config` with the MyGet feed, with the credential supplied by environment rather than
   committed; make the Docker build see it.
4. Add `IExpenseAccessTokenProvider` in Application; implement it in Infrastructure over
   `IAuthenticationClient`, with per-company caching and `Result<T>` at the boundary.
5. Attach it to the Expense API `HttpClient` through a `DelegatingHandler`; retire `ExpenseApi:ApiKey`.
6. Configuration and secret handling; `IdentitySDK` section wired through Docker environment.
7. Tests: token cache hit/expiry/refresh, failure translating to a refusal rather than an exception,
   an architecture test that no project outside `Justina.Expense.Infrastructure` references the SDK,
   and a scrubbing test that no log line ever carries `client_secret`.
8. Keep `ExpenseApi:Mode=Stub` working throughout, so the mock path stays available.

## 7. Open questions for you

1. **Credentials** — which environment do we point at (dev/UAT), and what are `ClientID`, `ClientSecret`
   and `Scope`? Nothing can be verified live without them. Send them out of band, not in chat.
2. **Feed access** — is the MyGet auth GUID in the other repositories' `nuget.config` still valid, and
   is it acceptable to use it in the Docker build, or is there a proper credential?
3. **Trimming** — is B agreed, or do you want the SDK byte-identical to upstream (A) with the secret
   logging handled some other way?
4. **Scope now** — does this replace the mocks entirely, or run alongside them behind
   `ExpenseApi:Mode` until the real Expense API contract lands?
