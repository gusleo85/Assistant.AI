using System.Reflection;
using Justina.Core.Application.Abstractions;
using Justina.Core.Application.Messaging;
using NetArchTest.Rules;
using Shouldly;

namespace Justina.ArchitectureTests;

/// <summary>
/// The CQRS rules from the plan (§14): commands may change state, queries must not.
/// </summary>
public class CqrsTests
{
    private static readonly Assembly[] ApplicationAssemblies =
    [
        typeof(IDispatcher).Assembly,
        typeof(Expense.Application.Commands.ConfirmReceiptCommand).Assembly,
        typeof(Recruitment.Application.IRecruitmentApiClient).Assembly,
    ];

    private static IEnumerable<Type> QueryHandlers => ApplicationAssemblies
        .SelectMany(a => a.GetTypes())
        .Where(t => t is { IsClass: true, IsAbstract: false })
        .Where(t => t.GetInterfaces().Any(i =>
            i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IQueryHandler<,>)))
        // The authorization decorator is a query handler by type but is not a use case.
        .Where(t => !t.Name.EndsWith("Decorator", StringComparison.Ordinal));

    /// <summary>
    /// A query handler that can reach the unit of work can commit a change. Removing the dependency is
    /// what makes "queries do not mutate" structural rather than a convention.
    /// </summary>
    [Fact]
    public void Query_handlers_cannot_reach_the_unit_of_work()
    {
        var offenders = QueryHandlers
            .Where(handler => handler
                .GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Any(p => p.ParameterType == typeof(IUnitOfWork)))
            .Select(t => t.Name)
            .ToList();

        offenders.ShouldBeEmpty(
            $"query handlers must not depend on IUnitOfWork: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Every_query_handler_is_actually_discovered()
    {
        // Guards the test above: a filter that silently matches nothing would pass forever.
        QueryHandlers.ShouldNotBeEmpty();
    }

    /// <summary>
    /// Commands that create something external must declare an idempotency key, or a retry duplicates it (§33).
    /// </summary>
    [Fact]
    public void The_confirm_command_is_idempotent()
    {
        typeof(IIdempotentCommand)
            .IsAssignableFrom(typeof(Expense.Application.Commands.ConfirmReceiptCommand))
            .ShouldBeTrue();

        typeof(IIdempotentCommand)
            .IsAssignableFrom(typeof(Expense.Application.Commands.ReceiveReceiptCommand))
            .ShouldBeTrue();
    }

    /// <summary>Every state-changing expense command is behind a capability check (§34).</summary>
    [Fact]
    public void Expense_commands_all_require_a_capability()
    {
        var commands = typeof(Expense.Application.Commands.ConfirmReceiptCommand).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => t.GetInterfaces().Any(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>)))
            .ToList();

        commands.ShouldNotBeEmpty();

        var unprotected = commands
            .Where(t => !typeof(IRequireCapability).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToList();

        unprotected.ShouldBeEmpty(
            $"these commands declare no required capability: {string.Join(", ", unprotected)}");
    }

    [Fact]
    public void Application_layers_expose_no_public_type_named_Service_that_touches_infrastructure()
    {
        // Guards against a God service creeping back in (§13).
        var result = Types.InAssembly(typeof(Expense.Application.Commands.ConfirmReceiptCommand).Assembly)
            .That().HaveNameEndingWith("Service")
            .ShouldNot().HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "System.Net.Http")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            string.Join(", ", result.FailingTypeNames ?? []));
    }
}
