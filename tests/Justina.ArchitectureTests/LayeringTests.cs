using System.Reflection;
using NetArchTest.Rules;
using Shouldly;

namespace Justina.ArchitectureTests;

/// <summary>
/// The layering rules from the plan, enforced by the build rather than by review (§6, §12, §15).
/// </summary>
public class LayeringTests
{
    private static readonly Assembly CoreDomain = typeof(Core.Domain.DomainException).Assembly;
    private static readonly Assembly CoreApplication = typeof(Core.Application.Messaging.IDispatcher).Assembly;
    private static readonly Assembly ExpenseDomain = typeof(Expense.Domain.Receipt).Assembly;
    private static readonly Assembly ExpenseApplication = typeof(Expense.Application.Commands.ConfirmReceiptCommand).Assembly;
    private static readonly Assembly RecruitmentDomain = typeof(Recruitment.Domain.CandidateSearchCriteria).Assembly;
    private static readonly Assembly RecruitmentApplication = typeof(Recruitment.Application.IRecruitmentApiClient).Assembly;

    public static TheoryData<string, Assembly> DomainAndApplicationAssemblies => new()
    {
        { "Justina.Core.Domain", CoreDomain },
        { "Justina.Core.Application", CoreApplication },
        { "Justina.Expense.Domain", ExpenseDomain },
        { "Justina.Expense.Application", ExpenseApplication },
        { "Justina.Recruitment.Domain", RecruitmentDomain },
        { "Justina.Recruitment.Application", RecruitmentApplication },
    };

    /// <summary>
    /// Business logic must not know about infrastructure. This is the rule that keeps the OpenAI, channel
    /// and database choices swappable without touching a domain rule (§12).
    /// </summary>
    [Theory]
    [MemberData(nameof(DomainAndApplicationAssemblies))]
    public void Domain_and_application_layers_do_not_depend_on_infrastructure(string name, Assembly assembly)
    {
        var result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "Microsoft.Data.SqlClient",
                "System.Net.Http",
                "Justina.Core.Infrastructure",
                "Justina.Expense.Infrastructure",
                "Justina.Recruitment.Infrastructure",
                "PDFtoImage",
                "UglyToad.PdfPig",
                "Serilog")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Explain(name, result));
    }

    [Theory]
    [MemberData(nameof(DomainAndApplicationAssemblies))]
    public void Layers_do_not_read_configuration_directly(string name, Assembly assembly)
    {
        var result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.Extensions.Configuration")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Explain(name, result));
    }

    /// <summary>A domain model has no dependencies at all beyond the shared core (§12).</summary>
    [Fact]
    public void Core_domain_depends_on_nothing_of_ours()
    {
        var result = Types.InAssembly(CoreDomain)
            .ShouldNot()
            .HaveDependencyOnAny("Justina.Core.Application", "Justina.Expense", "Justina.Recruitment")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Explain("Justina.Core.Domain", result));
    }

    [Fact]
    public void Expense_never_depends_on_Recruitment()
    {
        foreach (var assembly in new[] { ExpenseDomain, ExpenseApplication, typeof(Expense.Infrastructure.ExpenseInfrastructureServiceCollectionExtensions).Assembly })
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOn("Justina.Recruitment")
                .GetResult();

            result.IsSuccessful.ShouldBeTrue(Explain(assembly.GetName().Name!, result));
        }
    }

    [Fact]
    public void Recruitment_never_depends_on_Expense()
    {
        foreach (var assembly in new[] { RecruitmentDomain, RecruitmentApplication, typeof(Recruitment.Infrastructure.RecruitmentApiClient).Assembly })
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOn("Justina.Expense")
                .GetResult();

            result.IsSuccessful.ShouldBeTrue(Explain(assembly.GetName().Name!, result));
        }
    }

    private static string Explain(string assemblyName, TestResult result)
    {
        var offenders = result.FailingTypeNames is null ? [] : result.FailingTypeNames.ToList();

        return $"{assemblyName} violated a layering rule. Offending types: {string.Join(", ", offenders)}";
    }
}
