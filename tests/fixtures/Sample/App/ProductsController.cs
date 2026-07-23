using Library;

namespace App;

/// <summary>ASP.NET controller stand-in. Exercises the AspNetCoreAdapter via the stand-in Controller base type.</summary>
public class ProductsController : Controller
{
    public Product GetProduct(int id) => new Product(id, "Widget");
}
