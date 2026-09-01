using Justina.Core.Application.Messaging;
using Justina.Expense.Application.Abstractions;
using Justina.Expense.Application.Commands;
using Justina.Expense.Application.Queries;
using Justina.Expense.Application.Receipts;
using Microsoft.Extensions.DependencyInjection;

namespace Justina.Expense.Application;

public static class ExpenseApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddExpenseApplication(this IServiceCollection services)
    {
        // Every handler loads receipts through this, so the conversation-ownership check cannot be
        // forgotten in a new use case.
        services.AddScoped<IReceiptAccess, ReceiptAccess>();
        services.AddScoped<IReceiptSubmissionService, ReceiptSubmissionService>();

        services.AddCommandHandler<ReceiveReceiptCommand, ReceiveReceiptResult, ReceiveReceiptCommandHandler>();
        services.AddCommandHandler<ExtractReceiptCommand, ReceiptExtractionOutcome, ExtractReceiptCommandHandler>();
        services.AddCommandHandler<UpdateReceiptCommand, ReceiptSnapshot, UpdateReceiptCommandHandler>();
        services.AddCommandHandler<ConfirmReceiptCommand, ReceiptSnapshot, ConfirmReceiptCommandHandler>();
        services.AddCommandHandler<CancelReceiptCommand, ReceiptSnapshot, CancelReceiptCommandHandler>();
        services.AddCommandHandler<SubmitExpenseCommand, ReceiptSnapshot, SubmitExpenseCommandHandler>();

        services.AddQueryHandler<GetReceiptQuery, ReceiptSnapshot, GetReceiptQueryHandler>();
        services.AddQueryHandler<GetReceiptStatusQuery, ReceiptStatus, GetReceiptStatusQueryHandler>();
        services.AddQueryHandler<GetActiveExtractionQuery, ReceiptExtractionOutcome, GetActiveExtractionQueryHandler>();
        services.AddQueryHandler<GetExpenseOptionsQuery, ExpenseOptions, GetExpenseOptionsQueryHandler>();

        return services;
    }
}
