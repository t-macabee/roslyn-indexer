using Library;

namespace App;

/// <summary>EF Core DbContext stand-in. Exercises the EfCoreAdapter via the stand-in DbContext base type.</summary>
public class AppDbContext : DbContext
{
    public List<Product> Products { get; set; } = [];
    public List<Order> Orders { get; set; } = [];
}
