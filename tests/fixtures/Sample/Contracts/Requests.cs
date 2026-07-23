using MediatR;

namespace Contracts;

public record GetProductQuery(int ProductId) : IRequest<ProductResult>;

public record ProductResult(int Id, string Name, decimal Price);

public record ProductCreatedNotification(int ProductId, string Name) : INotification;
