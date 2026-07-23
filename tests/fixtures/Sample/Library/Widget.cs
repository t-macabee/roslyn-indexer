namespace Library;

/// <summary>Partial class — Part 1. Exercises brace/span guards for partial types.</summary>
public partial class Widget
{
    public string Name { get; set; } = "";

    public string GetLabel() => Name;
}
