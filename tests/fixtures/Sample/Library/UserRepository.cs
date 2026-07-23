namespace Library;

public class User
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
}

/// <summary>Concrete repository. Exercises structural edges for generic base class usage.</summary>
public class UserRepository : Repository<User>
{
    public User? FindByName(string name) => _items.FirstOrDefault(u => u.Name == name);
}
