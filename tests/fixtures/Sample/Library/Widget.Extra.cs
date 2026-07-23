namespace Library;

/// <summary>Partial class — Part 2. Exercises brace/span guards across files.</summary>
public partial class Widget
{
    public int Version { get; set; } = 1;

    public string GetFullLabel() => $"{Name} v{Version}";
}
