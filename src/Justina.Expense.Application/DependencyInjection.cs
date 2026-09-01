using Justina.Core.Application.Messaging;
using Justina.Expense.Application.Commands;
using Justina.Expense.Application.Queries;
using Justina.Expense.Application.Receipts;
using Microsoft.Extensions.DependencyInjection;

namespace Justina.Expense.Application;

public static class ExpenseApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddExpenseApplication(this IServiceCollection services)
    {
        services.AddScoped<IReceiptSubmissionService, ReceiptSubmissionService>();

        services.AddCommandHandler<ReceiveReceiptCommand, ReceiveReceiptResult, ReceiveReceiptCommandHandler>();
        services.AddCommandHandler<ExtractReceiptCommand, ReceiptExtractionOutcome, ExtractReceiptCommandHandler>();
        services.AddCommandHandler<UpdateReceiptCommand, ReceiptSnapshot, UpdateReceiptCommandHandler>();
        services.AddCommandHandler<ConfirmReceiptCommand, ReceiptSnapshot, ConfirmReceiptCommandHandler>();
        services.AddCommandHandler<CancelReceiptCommand, ReceiptSnapshot, CancelReceiptCommandHandler>();
        services.AddCommandHandler<SubmitExpenseCommand, ReceiptSnapshot, SubmitExpenseCommandHandler>();

        services.AddQueryHandler<GetReceiptQuery, ReceiptSnapshot, GetReceiptQueryHandler>();
        services.AddQueryHandler<GetReceiptStatusQuery, ReceiptStatus, GetReceiptStatusQueryHandler>();

        return services;
    }
}
