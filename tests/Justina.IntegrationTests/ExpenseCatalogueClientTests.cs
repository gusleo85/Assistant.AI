using Justina.Expense.Application.Abstractions;
using Justina.Expense.Infrastructure.Api;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Justina.IntegrationTests;

/// <summary>
/// Exercises the live catalogue path against a stub server speaking the routes and shapes verified in
/// expense-api: <c>/expense/v1/Categories/list/{organizationId}</c> and the matching taxes route, both
/// returning a bare <c>ListItemResponse</c> array in camelCase.
/// </summary>
public sealed class ExpenseCatalogueClientTests : IDisposable
{
    private static readonly ExpenseTenant Tenant = new(
        Guid.Parse("1ba47eac-7ae7-4270-a3b8-a935f30c53ee"),
        "1BA47EAC7AE74270A3B8A935F30C53EE",
        Guid.Parse("4b07c8bf-dda4-40b7-8042-ceaea8ed3342"));

    private const string CategoriesJson = """
        [
          { "id": "279555fd-5119-4d3c-b94d-88d5d03cceb5", "name": "Meals and Entertainment",
            "attribute": "False", "description": "", "isDefault": false },
          { "id": "9ef07585-4f43-43ce-a14d-1070ff941c43", "name": "Medical Expense",
            "attribute": "False", "description": "", "isDefault": false }
        ]
        """;

    private const string TaxesJson = """
        [
          { "id": "a9e8917d-54bc-4444-b014-55d221ef693c", "name": "GST9",
            "attribute": "9.00", "description": "", "isDefault": false }
        ]
        """;

    private readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    private ExpenseCatalogueClient Client(int timeoutSeconds = 30)
    {
        var options = Options.Create(new ExpenseApiOptions
        {
            Mode = ExpenseApiMode.Live,
            BaseUrl = _server.Urls[0],
            TimeoutSeconds = timeoutSeconds,
        });

        var httpClient = new HttpClient
        {
            BaseAddress = new Uri($"{_server.Urls[0].TrimEnd('/')}/"),
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        };

        return new ExpenseCatalogueClient(httpClient, options, NullLogger<ExpenseCatalogueClient>.Instance);
    }

    private void GivenCatalogue(string categories = CategoriesJson, string taxes = TaxesJson)
    {
        _server
            .Given(Request.Create().WithPath($"/expense/v1/Categories/list/{Tenant.OrganizationId}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json").WithBody(categories));

        _server
            .Given(Request.Create().WithPath($"/expense/v1/Taxes/list/{Tenant.OrganizationId}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json").WithBody(taxes));
    }

    [Fact]
    public async Task The_organization_id_goes_in_the_path_so_a_system_token_is_enough()
    {
        GivenCatalogue();

        var catalogue = await Client().GetAsync(Tenant, CancellationToken.None);

        catalogue.Categories.Count.ShouldBe(2);
        catalogue.FindCategory("Meals and Entertainment")!.Id
            .ShouldBe(Guid.Parse("279555fd-5119-4d3c-b94d-88d5d03cceb5"));

        _server.LogEntries
            .Select(entry => entry.RequestMessage?.Path ?? string.Empty)
            .ShouldContain($"/expense/v1/Categories/list/{Tenant.OrganizationId}");
    }

    [Fact]
    public async Task A_tax_rate_keeps_the_scale_the_api_sent()
    {
        GivenCatalogue();

        var catalogue = await Client().GetAsync(Tenant, CancellationToken.None);

        catalogue.Taxes.Single().Label.ShouldBe("GST9 (9.00%)");
        catalogue.Taxes.Single().Rate.ShouldBe(9.00m);
    }

    [Fact]
    public async Task A_failing_endpoint_yields_an_empty_catalogue_rather_than_an_error()
    {
        // Extraction must still run: the alternative is losing a receipt the user already sent.
        _server
            .Given(Request.Create().WithPath("/expense/v1/Categories/list/*").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));

        _server
            .Given(Request.Create().WithPath("/expense/v1/Taxes/list/*").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));

        var catalogue = await Client().GetAsync(Tenant, CancellationToken.None);

        catalogue.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task Nonsense_in_the_response_body_is_an_empty_catalogue_not_a_crash()
    {
        GivenCatalogue(categories: "<html>maintenance</html>", taxes: "<html>maintenance</html>");

        var catalogue = await Client().GetAsync(Tenant, CancellationToken.None);

        catalogue.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task A_cached_catalogue_is_not_fetched_twice()
    {
        GivenCatalogue();

        var cache = new CachingExpenseCatalogue(
            Client(),
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new ExpenseApiOptions { CatalogueCacheMinutes = 10 }));

        await cache.GetAsync(Tenant, CancellationToken.None);
        await cache.GetAsync(Tenant, CancellationToken.None);

        _server.LogEntries.Count(entry => (entry.RequestMessage?.Path ?? string.Empty).Contains("Categories")).ShouldBe(1);
    }

    [Fact]
    public async Task Two_organizations_never_share_a_cache_entry()
    {
        // A shared entry would put one company's categories into another company's prompt.
        GivenCatalogue();

        var other = Tenant with { OrganizationId = Guid.Parse("99999999-9999-4999-8999-999999999999") };

        _server
            .Given(Request.Create().WithPath($"/expense/v1/Categories/list/{other.OrganizationId}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                    [{ "id": "11111111-1111-4111-8111-111111111111", "name": "Other Company Only",
                       "attribute": "False", "description": "", "isDefault": false }]
                    """));

        _server
            .Given(Request.Create().WithPath($"/expense/v1/Taxes/list/{other.OrganizationId}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json").WithBody("[]"));

        var cache = new CachingExpenseCatalogue(
            Client(),
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new ExpenseApiOptions { CatalogueCacheMinutes = 10 }));

        var first = await cache.GetAsync(Tenant, CancellationToken.None);
        var second = await cache.GetAsync(other, CancellationToken.None);

        first.FindCategory("Meals and Entertainment").ShouldNotBeNull();
        first.FindCategory("Other Company Only").ShouldBeNull();
        second.FindCategory("Other Company Only").ShouldNotBeNull();
        second.FindCategory("Meals and Entertainment").ShouldBeNull();
    }

    [Fact]
    public async Task An_empty_catalogue_is_never_cached()
    {
        // Empty means the lookup failed. Caching that would stretch a brief outage into minutes of
        // unconstrained extraction after the API had already recovered.
        _server
            .Given(Request.Create().WithPath("/expense/v1/Categories/list/*").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(503));

        _server
            .Given(Request.Create().WithPath("/expense/v1/Taxes/list/*").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(503));

        var cache = new CachingExpenseCatalogue(
            Client(),
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new ExpenseApiOptions { CatalogueCacheMinutes = 10 }));

        await cache.GetAsync(Tenant, CancellationToken.None);
        await cache.GetAsync(Tenant, CancellationToken.None);

        _server.LogEntries.Count(entry => (entry.RequestMessage?.Path ?? string.Empty).Contains("Categories")).ShouldBe(2);
    }
}
