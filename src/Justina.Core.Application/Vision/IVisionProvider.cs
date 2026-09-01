using Justina.Core.Application.Documents;
using Justina.Core.Domain.Results;

namespace Justina.Core.Application.Vision;

/// <summary>
/// What the caller wants extracted. The schema is supplied by the calling domain, so Vision stays a
/// shared Justina capability rather than an Expense-specific one (§20).
/// </summary>
public sealed record VisionRequest(
    ProcessedDocument Document,
    string SchemaName,
    string JsonSchema,
    string Instruction);

public sealed record VisionExtractionResult(
    string Json,
    string Model,
    int? InputTokens,
    int? OutputTokens);

/// <summary>
/// One interface, one implementation today (§21). Document content is passed to the provider as data;
/// it is never concatenated into the instruction, so text inside a receipt cannot act as a command (§38).
/// </summary>
public interface IVisionProvider
{
    Task<Result<VisionExtractionResult>> ExtractAsync(VisionRequest request, CancellationToken cancellationToken);
}
