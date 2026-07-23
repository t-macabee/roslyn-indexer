namespace Library;

public class Square : IShape
{
    public double Side { get; set; }

    public double GetArea() => Side * Side;
}
