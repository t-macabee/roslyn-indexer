namespace App;

/// <summary>Positional records with semicolon body (no braces).
/// Exercises C1: the extractor must handle record declarations that end with a semicolon.</summary>
public record Product(int Id, string Name);

public record Order(int Id, decimal Amount);
