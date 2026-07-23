using Contracts;
using MediatR;

namespace App;

/// <summary>MediatR request handler. Exercises the MediatRAdapter Handles edge
/// across assemblies (request in Contracts, handler in App).</summary>
public class GetProductHandler : IRequestHandler<GetProductQuery, ProductResult>
{
    public Task<ProductResult> Handle(GetProductQuery request, CancellationToken cancellationToken)
    {
        var result = new ProductResult(request.ProductId, "Widget", 9.99m);
        return Task.FromResult(result);
    }
}
