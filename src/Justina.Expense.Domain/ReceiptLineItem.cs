namespace Justina.Expense.Domain;

public sealed class ReceiptLineItem
{
    // EF Core materialization.
    private ReceiptLineItem()
    {
        Description = string.Empty;
    }

    public ReceiptLineItem(string description, decimal quantity, decimal unitPrice, decimal amount)
    {
        Id = Guid.NewGuid();
        Description = description;
        Quantity = quantity;
        UnitPrice = unitPrice;
        Amount = amount;
    }

    public Guid Id { get; private set; }

    public Guid ReceiptId { get; private set; }

    public string Description { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal UnitPrice { get; private set; }

    public decimal Amount { get; private set; }
}
