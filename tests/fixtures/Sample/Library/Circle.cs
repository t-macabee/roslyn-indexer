namespace Library;

public class Circle : IShape
{
    public double Radius { get; set; }

    public double GetArea() => Math.PI * Radius * Radius;
}
