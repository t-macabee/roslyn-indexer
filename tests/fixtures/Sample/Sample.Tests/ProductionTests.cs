namespace Sample.Tests;

public class ProductionTests
{
    [Fact]
    public void GetProduct_ReturnsProduct()
    {
        var product = new App.Product(1, "Widget");
        Assert.Equal(1, product.Id);
        Assert.Equal("Widget", product.Name);
    }
}
